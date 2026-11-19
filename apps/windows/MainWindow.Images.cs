using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NopeView.App;

// Image pipeline: open, decode, animation, and navigation.
public sealed partial class MainWindow : Window
{
    private void OpenImageFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index + 1 < args.Length; index++)
        {
            if (args[index] == "--open")
            {
                _ = OpenImageAsync(args[index + 1]);
                return;
            }
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(OpenImageViaPickerAsync, Strings.OpenTitle);
    }

    private async Task OpenImageViaPickerAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = "Open image"
        };

        foreach (var extension in SupportedExtensions())
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            picker.FileTypeFilter.Add(normalized);
        }

        var result = await picker.PickSingleFileAsync();
        if (result is null)
        {
            return;
        }
        await OpenImageAsync(result.Path);
    }

    /// <summary>
    /// Extensions the router can decode. The router discovers its formats once
    /// at startup, so the list is built on first use and reused afterwards.
    /// </summary>
    private IReadOnlyList<string> SupportedExtensions()
    {
        if (_supportedExtensions is not null)
        {
            return _supportedExtensions;
        }

        var extensions = new List<string>();
        if (_routerContext != IntPtr.Zero)
        {
            foreach (var format in RouterApi.ListFormats(_routerContext))
            {
                if (!format.CanDecode)
                {
                    continue;
                }
                foreach (var extension in format.Extensions.Split(
                             ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    extensions.Add(extension);
                }
            }
        }

        _supportedExtensions = extensions;
        return extensions;
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        await RunCommandAsync(() => OpenDroppedFileAsync(e), Strings.OpenTitle);
    }

    private async Task OpenDroppedFileAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null)
        {
            await OpenImageAsync(file.Path);
        }
    }

    private async Task OpenImageAsync(string path)
    {
        StopAnimation();
        _frameCount = 1;
        _frameIndex = 0;
        UpdateAnimationControls();
        _directory = ImageDirectory.Create(path, SupportedExtensions());
        UpdateNavigationState();
        await ShowCurrentImageAsync();
    }

    private async Task ShowCurrentImageAsync()
    {
        if (_directory is null)
        {
            return;
        }

        StopAnimation();
        await DecodeAndShowAsync(_directory.CurrentPath);
        UpdateNavigationState();
    }

    private async Task DecodeAndShowAsync(
        string path,
        uint frameIndex = 0u,
        bool resetView = true,
        bool fullResolution = false)
    {
        if (_routerContext == IntPtr.Zero)
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
            // Only a full open shows the indicator. Animation frames are short
            // enough that a ring would flicker on every step.
            ErrorPanel.IsOpen = false;
            SetLoading(true);
        }

        var gateHeld = false;
        try
        {
            await _decodeGate.WaitAsync(token);
            gateHeld = true;
            token.ThrowIfCancellationRequested();

            // Decoding, the pixel copy, and the premultiply step all run on a
            // worker thread. Leaving them on the UI thread froze the window for
            // the whole decode and prevented a loading state from rendering.
            var context = _routerContext;
            var maxDimension = fullResolution ? 0 : PreviewEdge();
            var frame = await Task.Run(
                () => DecodeFrame(context, path, frameIndex, maxDimension), token);

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
                await _frameSource.SetBitmapAsync(frame.Bitmap);
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

            _imagePixelWidth = frame.SourceWidth;
            _imagePixelHeight = frame.SourceHeight;
            _previewActive = frame.SourceWidth > frame.BitmapWidth;
            _frameIndex = frameIndex;
            _frameCount = Math.Max(1u, frame.FrameCount);
            _frameDelayMs = frame.FrameDelayMs;
            SetViewControlsEnabled(true);
            UpdateFileStatus(path);
            UpdateViewStatus();
            UpdateAnimationControls();
            UpdateImageAutomationName(path);

            if (resetView)
            {
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
            frame.Bitmap = PixelBuffer.ToSoftwareBitmap(image, BitmapAlphaMode.Premultiplied);
            frame.BitmapWidth = image.Width;
            frame.SourceWidth = image.SourceWidth > 0 ? image.SourceWidth : image.Width;
            frame.SourceHeight = image.SourceHeight > 0 ? image.SourceHeight : image.Height;
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
        internal int SourceWidth;
        internal int SourceHeight;
        internal uint FrameCount;
        internal uint FrameDelayMs;
        internal string? Error;
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
    /// Replaces the preview bitmap with a full-size decode once the view
    /// magnifies its pixels. One attempt per load; animated frames keep the
    /// preview so playback does not decode full frames.
    /// </summary>
    private async Task UpgradeToFullResolutionAsync()
    {
        if (!_previewActive || _frameCount > 1 || _directory is null)
        {
            return;
        }

        // Cleared before the decode so overlapping zoom input cannot queue a
        // second full decode; the decode result sets it again.
        _previewActive = false;
        await DecodeAndShowAsync(
            _directory.CurrentPath, _frameIndex, resetView: false, fullResolution: true);
    }

    private void SetLoading(bool loading)
    {
        var visible = LoadingRing.Visibility == Visibility.Visible;
        if (loading == visible)
        {
            return;
        }

        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        // The rotation is a dependent animation, so it only needs to run while
        // the indicator is on screen.
        if (loading)
        {
            _loadingAnimation.Begin();
        }
        else
        {
            _loadingAnimation.Stop();
        }
    }

    /// <summary>
    /// Spins the dashed loading ring. A classic storyboard is used because a
    /// ProgressRing's Lottie visual crashes the current WinUI build.
    /// </summary>
    private Storyboard CreateLoadingAnimation()
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1.2)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, LoadingSpinnerRotation);
        Storyboard.SetTargetProperty(spin, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(spin);
        return storyboard;
    }

    private void UpdateImageAutomationName(string path)
    {
        var name = "Current image: " + Path.GetFileName(path);
        if (_previewActive)
        {
            // Tells assistive tech that the displayed bitmap is downscaled.
            name += " (preview)";
        }
        if (_frameCount > 1)
        {
            name += $" [frame {_frameIndex + 1}/{_frameCount}]";
        }
        AutomationProperties.SetName(ImageView, name);
    }

    private void AnimationTimer_Tick(object? sender, object e)
    {
        _ = RunCommandAsync(AdvanceAnimationAsync, Strings.AnimationTitle);
    }

    private async Task AdvanceAnimationAsync()
    {
        _animationTimer.Stop();
        if (!_isAnimationPlaying || _animationBusy || _directory is null)
        {
            return;
        }

        if (_frameCount <= 1)
        {
            StopAnimation();
            return;
        }

        var next = _frameIndex + 1;
        if (next >= _frameCount)
        {
            if (!_animationLoop)
            {
                StopAnimation();
                return;
            }
            next = 0;
        }

        var path = _directory.CurrentPath;
        _animationBusy = true;
        try
        {
            await DecodeAndShowAsync(path, next, resetView: false);
        }
        finally
        {
            _animationBusy = false;
        }

        if (_isAnimationPlaying)
        {
            ScheduleNextFrame();
        }
    }

    private void ScheduleNextFrame()
    {
        if (!_isAnimationPlaying || _frameCount <= 1)
        {
            return;
        }

        if (!_animationClock.IsRunning)
        {
            _animationClock.Restart();
            _animationDeadline = TimeSpan.Zero;
        }

        var delay = TimeSpan.FromMilliseconds(
            Math.Clamp((double)_frameDelayMs, 20.0, 10000.0));
        var now = _animationClock.Elapsed;

        // Add the delay to the previous deadline rather than to the moment the
        // decode finished, so timer latency stays out of the frame duration.
        _animationDeadline += delay;
        if (_animationDeadline < now)
        {
            // A slow decode put us behind schedule. Resume from now instead of
            // firing a burst of catch-up frames.
            _animationDeadline = now;
        }

        _animationTimer.Interval = _animationDeadline - now;
        _animationTimer.Start();
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        _isAnimationPlaying = false;
        _animationBusy = false;
        UpdateAnimationControls();
    }

    private void UpdateAnimationControls()
    {
        var animated = _frameCount > 1;
        AnimationControls.Visibility = animated ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.IsEnabled = animated;
        LoopButton.IsEnabled = animated;
        PlayPauseLabel.Text = _isAnimationPlaying ? "Pause" : "Play";
        LoopLabel.Text = _animationLoop ? "Loop on" : "Loop off";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_frameCount <= 1)
        {
            return;
        }

        if (_isAnimationPlaying)
        {
            StopAnimation();
            return;
        }

        if (_frameIndex + 1 >= _frameCount)
        {
            _frameIndex = 0;
        }
        _isAnimationPlaying = true;
        UpdateAnimationControls();
        ScheduleNextFrame();
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _animationLoop = !_animationLoop;
        UpdateAnimationControls();
    }

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = _directory?.HasPrevious == true;
        NextButton.IsEnabled = _directory?.HasNext == true;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MovePreviousAsync, Strings.OpenTitle);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MoveNextAsync, Strings.OpenTitle);
    }

    private async void PreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MovePreviousAsync, Strings.OpenTitle);
    }

    private async void NextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MoveNextAsync, Strings.OpenTitle);
    }

    private void PlayPauseAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_frameCount <= 1)
        {
            return;
        }
        args.Handled = true;
        TogglePlayPause();
    }

    private async Task MovePreviousAsync()
    {
        if (_directory is not null && _directory.MovePrevious())
        {
            await ShowCurrentImageAsync();
        }
    }

    private async Task MoveNextAsync()
    {
        if (_directory is not null && _directory.MoveNext())
        {
            await ShowCurrentImageAsync();
        }
    }

    private void UpdateFileStatus(string path)
    {
        var name = Path.GetFileName(path);
        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var dimensions = $"{_imagePixelWidth} x {_imagePixelHeight}";
        FileStatusText.Text = string.IsNullOrEmpty(format)
            ? $"{name} | {dimensions}"
            : $"{name} | {dimensions} | {format}";
    }
}
