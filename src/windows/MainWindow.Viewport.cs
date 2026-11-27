// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NopeView.App;

// Viewport: clip, layout status, fit ratio, and view state.
public sealed partial class MainWindow : Window
{
    // The clip is reused across resizes. Replacing it on every SizeChanged
    // allocates a new geometry while the window is being dragged.
    private RectangleGeometry? _viewportClip;
    private bool _viewStatusPending;
    private readonly DispatcherTimer _frameSizeTimer = new();

    // When the last size event arrived, so the next presented frame can report
    // how long the resize took to reach the screen.
    private double _resizeLatencyStartMs = -1;

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MarkInteraction("resize");
        if (UiPerf.IsEnabled)
        {
            _resizeLatencyStartMs = _renderingClock.Elapsed.TotalMilliseconds;
        }
        var bounds = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        if (_viewportClip is null)
        {
            _viewportClip = new RectangleGeometry { Rect = bounds };
            Viewport.Clip = _viewportClip;
        }
        else if (_viewportClip.Rect.Width != bounds.Width ||
                 _viewportClip.Rect.Height != bounds.Height)
        {
            // The clip itself must stay current: the image is only allowed to
            // paint inside this rectangle, so a delayed clip would show up as
            // a wrong edge during a live resize.
            _viewportClip.Rect = bounds;
        }

        // A live resize only rewrites the scale that carries the fit factor.
        // Re-sizing the frame here would relay out and repaint the bitmap on
        // every size event, which is what makes a snapped window stutter.
        // Shrinking is free: the frame keeps its larger surface and the scale
        // draws it down, which stays sharp. Growing is the only case that needs
        // more pixels, so that is the only case worth a bake.
        ApplyViewTransform();
        QueueViewStatusUpdate();

        var fit = GetFitScale(_imageFrameStyle);
        if (_frameFit > 0 && fit > 0 && fit > _frameFit * FrameSizeMaxStretch)
        {
            // Far beyond the tolerated softness: pay for the bake now instead
            // of showing a blurred image for the rest of the drag.
            UpdateImageFrameSize();
            return;
        }

        ScheduleFrameSizeBake();
    }

    // A live scale may stretch the baked bitmap this far before it looks soft.
    private const double FrameSizeMaxStretch = 2.0;

    // Below this share of the baked size the surface is holding pixels the
    // screen cannot use, so the settle bake trims it back.
    private const double FrameSizeMaxShrink = 0.4;

    // Settling time after the last size event before the frame is re-sized.
    private const double FrameSizeBakeDelayMs = 200;

    private void ScheduleFrameSizeBake()
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        _frameSizeTimer.Stop();
        _frameSizeTimer.Start();
    }

    private void FrameSizeTimer_Tick(object? sender, object e)
    {
        _frameSizeTimer.Stop();
        if (_closing)
        {
            return;
        }

        // Only two cases earn a bake: the frame is drawing the bitmap larger
        // than it holds, or it is holding far more pixels than the screen can
        // use. Everything between them keeps the size it has, because a
        // downscaled bitmap stays sharp and costs nothing to show.
        var fit = GetFitScale(_imageFrameStyle);
        if (_frameFit > 0 && fit > 0 &&
            (fit > _frameFit || fit < _frameFit * FrameSizeMaxShrink))
        {
            UpdateImageFrameSize();
        }

        UpdateViewStatus();
    }

    private void QueueViewStatusUpdate()
    {
        if (_viewStatusPending)
        {
            return;
        }

        _viewStatusPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _viewStatusPending = false;
                if (!_closing)
                {
                    UpdateViewStatus();
                }
            }))
        {
            _viewStatusPending = false;
        }
    }

    private void ResetView()
    {
        _view.Reset();
        ApplyViewTransform();
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        // A resize reaches this before any image exists; there is no view to
        // describe yet, so the line stays empty instead of claiming a fit.
        if (_imagePixelWidth == 0)
        {
            ViewStatusText.Text = string.Empty;
            return;
        }

        var rotation = Math.Round(_view.Rotation);
        var details = "";
        if (_frameCount > 1)
        {
            details += Localization.Format(
                "Status.FrameSuffix", _frameIndex + 1, _frameCount);
        }

        if (IsAtFit)
        {
            ViewStatusText.Text =
                Localization.Format("Status.ViewFit", rotation) + details;
            return;
        }

        var percent = _view.Zoom * FitToPixelRatio * 100.0;
        ViewStatusText.Text = Localization.Format(
            "Status.ViewPercent", Math.Round(percent), rotation) + details;
    }

    private void SetViewControlsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RotateLeftButton.IsEnabled = enabled;
        RotateRightButton.IsEnabled = enabled;
        FlipHorizontalButton.IsEnabled = enabled;
        FlipVerticalButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
        CopyFileMenuItem.IsEnabled = enabled;
        CopyImageMenuItem.IsEnabled = enabled;
        CopyPathMenuItem.IsEnabled = enabled;
        SaveAsMenuItem.IsEnabled = enabled;
        ShowInExplorerMenuItem.IsEnabled = enabled;
        FitMenuItem.IsEnabled = enabled;
        ActualSizeMenuItem.IsEnabled = enabled;
    }

    private double FitToPixelRatio
    {
        get
        {
            if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0 ||
                Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
            {
                return 1.0;
            }

            // The frame uses the same available box as the fit calculation,
            // including the inset that reserves room for a shadow. Deriving it
            // from the viewport keeps this exact while layout is still pending.
            var fitScale = GetFitScale(_imageFrameStyle);
            if (fitScale <= 0)
            {
                return 1.0;
            }

            var rasterizationScale = Viewport.XamlRoot?.RasterizationScale ?? 1.0;
            return fitScale * rasterizationScale;
        }
    }

    // Fit is zoom exactly 1, and gesture arithmetic leaves a little float
    // drift, so the readout and the preview upgrade share one tolerance.
    private const double FitTolerance = 0.001;

    /// <summary>
    /// True while the image is drawn at the size that fits the viewport.
    /// </summary>
    private bool IsAtFit => Math.Abs(_view.Zoom - 1.0) < FitTolerance;

    /// <summary>
    /// True while the image is drawn larger than the viewport, which is where
    /// a downscaled preview bitmap puts fewer pixels on screen than it claims.
    /// </summary>
    private bool IsMagnified => _view.Zoom > 1.0 + FitTolerance;

    /// <summary>
    /// True while the view is turned a quarter turn, where the bitmap's width
    /// and height on screen are the reverse of the file's.
    /// </summary>
    private bool IsQuarterTurn =>
        ((int)Math.Round(_view.Rotation / 90.0)) % 2 != 0;

    /// <summary>
    /// Keeps the image inside the viewport. The pan offset is the only part of
    /// the view that can carry the picture out of sight, so the slack on each
    /// axis is half of whatever the scaled image overhangs the viewport; with
    /// no overhang the image stays centered.
    /// </summary>
    private void ClampPan(double fit)
    {
        if (fit <= 0 ||
            _imagePixelWidth <= 0 ||
            _imagePixelHeight <= 0 ||
            _frameFit <= 0 ||
            Viewport.ActualWidth <= 0 ||
            Viewport.ActualHeight <= 0)
        {
            return;
        }

        var quarterTurn = IsQuarterTurn;
        var shownWidth =
            (quarterTurn ? _imagePixelHeight : _imagePixelWidth) * fit * _view.Zoom;
        var shownHeight =
            (quarterTurn ? _imagePixelWidth : _imagePixelHeight) * fit * _view.Zoom;

        var slackX = Math.Max(0, (shownWidth - Viewport.ActualWidth) / 2);
        var slackY = Math.Max(0, (shownHeight - Viewport.ActualHeight) / 2);
        _view.PanX = Math.Clamp(_view.PanX, -slackX, slackX);
        _view.PanY = Math.Clamp(_view.PanY, -slackY, slackY);
    }

    /// <summary>
    /// Zoom, rotation, flips and pan of the displayed image. Every gesture
    /// writes here and one ApplyTo call pushes the values into the render
    /// transform, so the numbers and the transform cannot drift apart.
    /// </summary>
    private sealed class ViewState
    {
        internal double Zoom = 1.0;
        internal double Rotation;
        internal double PanX;
        internal double PanY;
        internal bool FlipHorizontal;
        internal bool FlipVertical;

        internal void Reset()
        {
            Zoom = 1.0;
            Rotation = 0;
            PanX = 0;
            PanY = 0;
            FlipHorizontal = false;
            FlipVertical = false;
        }

        internal void ApplyTo(
            RotateTransform rotate,
            ScaleTransform scale,
            TranslateTransform translate,
            double compensation)
        {
            rotate.Angle = Rotation;
            var factor = Zoom * compensation;
            scale.ScaleX = FlipHorizontal ? -factor : factor;
            scale.ScaleY = FlipVertical ? -factor : factor;
            translate.X = PanX;
            translate.Y = PanY;
        }
    }
}
