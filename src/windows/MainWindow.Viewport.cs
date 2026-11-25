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

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MarkInteraction("resize");
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
        // The local size is baked once the resize settles, and only early when
        // the live scale would stretch the bitmap past a softness the eye can
        // catch or hold a surface far larger than the screen needs.
        ApplyViewTransform();
        QueueViewStatusUpdate();

        var fit = GetFitScale(_imageFrameStyle);
        if (_frameFit > 0 && fit > 0 &&
            (fit > _frameFit * FrameSizeMaxStretch ||
             fit < _frameFit * FrameSizeMaxShrink))
        {
            UpdateImageFrameSize();
            return;
        }

        ScheduleFrameSizeBake();
    }

    // A live scale may stretch the baked bitmap this far before it looks soft.
    private const double FrameSizeMaxStretch = 1.25;

    // ... and may shrink it this far before the frame holds more pixels than
    // the screen can use.
    private const double FrameSizeMaxShrink = 0.6;

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
        if (!_closing)
        {
            UpdateImageFrameSize();
            UpdateViewStatus();
        }
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
