// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml;
using NopeView.App.Native;
using Windows.Graphics.Imaging;

namespace NopeView.App;

// Decode path: router decode, preview sizing, and the loading state.
public sealed partial class MainWindow : Window
{
    private async Task DecodeAndShowAsync(
        string path,
        uint frameIndex = 0u,
        bool resetView = true,
        bool fullResolution = false,
        DecodedFrame? readyFrame = null)
    {
        var session = await RouterAsync();
        if (session.Context == IntPtr.Zero)
        {
            ShowError(Strings.RouterUnavailable);
            return;
        }

        var generation = ++_decodeGeneration;
        _decodeCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _decodeCancellation = cancellation;
        var token = cancellation.Token;

        if (resetView)
        {
            // A new image owns the view, and a frame prefetched for the old one
            // must not survive into it.
            DiscardPrefetch();

            // Only a full open shows the indicator. Animation frames are short
            // enough that a ring would flicker on every step.
            SetErrorBarOpen(false);
            SetLoading(true);
        }

        var gateHeld = false;
        var openClock = resetView ? UiPerf.Start() : null;
        try
        {
            await _decodeGate.WaitAsync(token);
            gateHeld = true;
            token.ThrowIfCancellationRequested();

            // Decoding, the pixel copy, and the premultiply step all run on a
            // worker thread. Leaving them on the UI thread froze the window for
            // the whole decode and prevented a loading state from rendering.
            var context = session.Context;

            // An animation decodes every step again, so it keeps a tighter
            // preview bound than a still image. The frame count only becomes
            // known from the first decode, which is why both signals count.
            var animated = frameIndex > 0 || _frameCount > 1;
            var maxDimension = fullResolution
                ? 0
                : animated
                    ? AnimatedPreviewEdge()
                    : PreviewEdge();

            // A frame decoded while the previous one was on screen skips the
            // worker step entirely, and only the handoff is left to do.
            var frame = readyFrame ?? await Task.Run(
                () =>
                {
                    var clock = UiPerf.Start();
                    var decoded = DecodeFrame(context, path, frameIndex, maxDimension);
                    UiPerf.Stop(
                        clock,
                        "decode",
                        $"{maxDimension} -> {decoded.BitmapWidth}x{decoded.BitmapHeight}");
                    return decoded;
                },
                token);

            if (generation != _decodeGeneration)
            {
                // A newer request already owns the view.
                frame.Bitmap?.Dispose();
                return;
            }

            if (frame.Error is not null)
            {
                FailDecode(Strings.OpenFailed(frame.Error));
                return;
            }

            if (frame.Bitmap is null)
            {
                FailDecode(Strings.DescribeDecodeStatus(frame.Status));
                return;
            }

            try
            {
                // Handing the pixels to the display is the one step of the open
                // path that runs on the UI thread, so its cost is recorded here
                // instead of being assumed.
                var clock = UiPerf.Start();
                await _frameSource.SetBitmapAsync(frame.Bitmap);
                UiPerf.Stop(
                    clock, "bitmap-upload", $"{frame.BitmapWidth}x{frame.BitmapHeight}");
            }
            finally
            {
                // The source copies the pixels, so the decoded bitmap is no
                // longer needed once the update completes.
                frame.Bitmap.Dispose();
            }

            if (generation != _decodeGeneration)
            {
                return;
            }

            if (!ReferenceEquals(ImageView.Source, _frameSource))
            {
                ImageView.Source = _frameSource;
            }

            var sourceSizeChanged =
                _imagePixelWidth != frame.SourceWidth ||
                _imagePixelHeight != frame.SourceHeight;
            var previewChanged =
                _previewActive != (frame.SourceWidth > frame.BitmapWidth);
            var frameCountChanged = _frameCount != Math.Max(1u, frame.FrameCount);

            _imagePixelWidth = frame.SourceWidth;
            _imagePixelHeight = frame.SourceHeight;
            _previewActive = frame.SourceWidth > frame.BitmapWidth;
            _frameIndex = frameIndex;
            _frameCount = Math.Max(1u, frame.FrameCount);
            _frameDelayMs = frame.FrameDelayMs;

            if (resetView)
            {
                // A new image owns the whole UI state: frame size, commands,
                // file readout and automation name all change together.
                UpdateImageFrameSize();
                SetViewControlsEnabled(true);
                UpdateFileStatus(path);
                UpdateAnimationControls();
                UpdateImageAutomationName(path);
                _lastFrameUiUpdate = Environment.TickCount64;

                // ResetView publishes the view status once the state above is
                // in place, so the open path does not update it separately.
                ResetView();
                if (_frameCount > 1)
                {
                    _isAnimationPlaying = true;
                    UpdateAnimationControls();
                    ScheduleNextFrame();
                }
                else
                {
                    StopAnimation();
                }
            }
            else
            {
                // A frame update only owns what actually changed. The file
                // readout, command availability and frame size stay untouched
                // unless the source itself changed.
                if (sourceSizeChanged)
                {
                    UpdateImageFrameSize();
                    UpdateFileStatus(path);
                }
                if (frameCountChanged)
                {
                    UpdateAnimationControls();
                }
                if (fullResolution || previewChanged || frameCountChanged)
                {
                    UpdateImageAutomationName(path);
                    _lastFrameUiUpdate = Environment.TickCount64;
                }
                else
                {
                    UpdateAnimationFrameUi(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this decode before it started. The
            // finally block still clears the loading state when it is current.
        }
        catch (Exception ex)
        {
            ShowError(Strings.OpenFailed(ex.Message));
            if (!resetView)
            {
                StopAnimation();
            }
        }
        finally
        {
            if (gateHeld)
            {
                _decodeGate.Release();
            }

            // A superseded open must not hide the indicator, because the newer
            // request is still decoding behind it.
            if (resetView && generation == _decodeGeneration)
            {
                SetLoading(false);

                // Everything an open waits for, from the request to the image
                // being on screen, which is the number a user actually feels.
                UiPerf.Stop(
                    openClock,
                    "open-total",
                    $"{_imagePixelWidth}x{_imagePixelHeight}");
            }

            if (ReferenceEquals(_decodeCancellation, cancellation))
            {
                _decodeCancellation = null;
            }
            cancellation.Dispose();
        }

        // Both failure paths share the same state repair: the preview bitmap is
        // still on screen after a failed full decode, and animation stops when a
        // frame fails instead of spinning on a broken source.
        void FailDecode(string message)
        {
            ShowError(message);
            if (fullResolution)
            {
                _previewActive = true;
            }
            if (!resetView)
            {
                StopAnimation();
            }
        }
    }

    /// <summary>
    /// Decodes one frame entirely on the calling thread. The router image is
    /// released here, so no native pointer ever crosses threads.
    /// </summary>
    private DecodedFrame DecodeFrame(IntPtr context, string path, uint frameIndex, int maxDimension)
    {
        var frame = new DecodedFrame();

        try
        {
            using var decoded = RouterImage.Decode(
                context, path, frameIndex, RouterApi.PixelFormatBgra8, maxDimension, out var status);
            if (decoded is null)
            {
                frame.Status = status;
                return frame;
            }

            var image = decoded.Value;

            // A phone photo stores its pixels as the sensor saw them and puts
            // the turn in EXIF. Turning them here rather than at display time
            // keeps the reported size, the fit and every later copy consistent.
            var orientation = ExifOrientation.Read(path);
            frame.Bitmap = PixelBuffer.ToSoftwareBitmap(
                image, BitmapAlphaMode.Premultiplied, orientation);
            frame.BitmapWidth = frame.Bitmap.PixelWidth;
            frame.BitmapHeight = frame.Bitmap.PixelHeight;

            var sourceWidth = image.SourceWidth > 0 ? image.SourceWidth : image.Width;
            var sourceHeight = image.SourceHeight > 0 ? image.SourceHeight : image.Height;
            if (ExifOrientation.SwapsAxes(orientation))
            {
                // The orientation turned the file, so its declared size turns
                // with it.
                frame.SourceWidth = sourceHeight;
                frame.SourceHeight = sourceWidth;
            }
            else
            {
                frame.SourceWidth = sourceWidth;
                frame.SourceHeight = sourceHeight;
            }

            frame.FrameCount = image.FrameCount;
            frame.FrameDelayMs = image.FrameDelayMs;
        }
        catch (Exception ex)
        {
            frame.Error = ex.Message;
        }

        return frame;
    }

    private sealed class DecodedFrame
    {
        internal SoftwareBitmap? Bitmap;
        internal int Status;
        internal int BitmapWidth;
        internal int BitmapHeight;
        internal int SourceWidth;
        internal int SourceHeight;
        internal uint FrameCount;
        internal uint FrameDelayMs;
        internal string? Error;
    }

    /// <summary>
    /// Longest viewport edge for an animated frame. Animation steps decode
    /// again for every frame, so they stop at a smaller edge than a still image.
    /// </summary>
    private int AnimatedPreviewEdge() =>
        Math.Min(PreviewEdge(), AnimationPreviewMaxEdge);

    /// <summary>
    /// Decodes the frame playback is about to need while the current frame is
    /// still on screen, so the step itself only has to hand pixels over.
    /// </summary>
    private void StartPrefetch(string path, uint frameIndex)
    {
        DiscardPrefetch();
        if (!_isAnimationPlaying || _frameCount <= 1 || _closing)
        {
            return;
        }

        var token = ++_prefetchToken;
        var edge = AnimatedPreviewEdge();
        _ = PrefetchFrameAsync(path, frameIndex, edge, token);
    }

    private async Task PrefetchFrameAsync(string path, uint frameIndex, int edge, int token)
    {
        try
        {
            var session = await RouterAsync();
            if (session.Context == IntPtr.Zero)
            {
                return;
            }

            DecodedFrame frame;
            await _decodeGate.WaitAsync();
            try
            {
                if (token != _prefetchToken)
                {
                    return;
                }
                frame = await Task.Run(() => DecodeFrame(session.Context, path, frameIndex, edge));
            }
            finally
            {
                _decodeGate.Release();
            }

            var bitmap = frame.Bitmap;
            if (bitmap is null || frame.Error is not null)
            {
                return;
            }

            // The result belongs to playback only while the token is current;
            // a paused or replaced animation drops it instead of leaking it.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing || token != _prefetchToken || !_isAnimationPlaying)
                {
                    bitmap.Dispose();
                    return;
                }
                _prefetchedFrame = frame;
                _prefetchedPath = path;
                _prefetchedIndex = frameIndex;
                _prefetchedEdge = edge;
            });
        }
        catch (Exception)
        {
            // A prefetch is an optimization: playback falls back to decoding the
            // frame on demand, so a failure needs no user-visible state.
        }
    }

    /// <summary>
    /// Hands the prefetched frame to the step that needs it, or drops it when
    /// it is not the frame playback is about to show.
    /// </summary>
    private DecodedFrame? TakePrefetch(string path, uint frameIndex)
    {
        var frame = _prefetchedFrame;
        if (frame is not null && _prefetchedPath == path &&
            _prefetchedIndex == frameIndex && _prefetchedEdge == AnimatedPreviewEdge())
        {
            _prefetchedFrame = null;
            _prefetchedPath = null;
            ++_prefetchToken;
            return frame;
        }

        DiscardPrefetch();
        return null;
    }

    private void DiscardPrefetch()
    {
        ++_prefetchToken;
        _prefetchedFrame?.Bitmap?.Dispose();
        _prefetchedFrame = null;
        _prefetchedPath = null;
    }

    /// <summary>
    /// Longest viewport edge in physical pixels, clamped so the preview stays
    /// sharp at fit on small displays and bounded on very large ones.
    /// </summary>
    private int PreviewEdge()
    {
        var scale = Viewport.XamlRoot?.RasterizationScale ?? Scale;
        var longest = Math.Max(Viewport.ActualWidth, Viewport.ActualHeight) * scale;
        return Math.Clamp((int)Math.Round(longest), PreviewMinEdge, PreviewMaxEdge);
    }

    /// <summary>
    /// The directory a full-resolution replacement would decode from, or null
    /// when the displayed bitmap is already the real pixels. Animated frames
    /// keep the preview, so they never qualify.
    /// </summary>
    private ImageDirectory? UpgradeSource =>
        _previewActive && _frameCount <= 1 ? _directory : null;

    /// <summary>
    /// Replaces the preview bitmap with a full-size decode once the view
    /// magnifies its pixels. One attempt per load; animated frames keep the
    /// preview so playback does not decode full frames.
    /// </summary>
    private async Task UpgradeToFullResolutionAsync()
    {
        if (UpgradeSource is not { } directory)
        {
            return;
        }

        // Cleared before the decode so overlapping zoom input cannot queue a
        // second full decode; the decode result sets it again.
        _previewActive = false;
        await DecodeAndShowAsync(
            directory.CurrentPath, _frameIndex, resetView: false, fullResolution: true);
    }

    private void SetLoading(bool loading)
    {
        var visible = LoadingRing.Visibility == Visibility.Visible;
        if (loading == visible)
        {
            return;
        }

        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;

        // The empty state and the ring share the same center, so the copy steps
        // aside while a decode runs and comes back only if no image arrived.
        EmptyState.Visibility = loading || _imagePixelWidth > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
