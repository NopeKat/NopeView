using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NopeView.App;

// Viewport: zoom, pan, rotate, pinch, and view status.
public sealed partial class MainWindow : Window
{
    // The clip is reused across resizes. Replacing it on every SizeChanged
    // allocates a new geometry while the window is being dragged.
    private RectangleGeometry? _viewportClip;
    private bool _viewStatusPending;

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
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

        // The frame size is layout state and has to follow this event. Only
        // the caption readout is safe to coalesce, because several resize
        // events can arrive before the dispatcher gets to the first one.
        UpdateImageFrameSize();
        QueueViewStatusUpdate();
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

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(Viewport);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // Precision touchpad pinch arrives as Ctrl+wheel, so anchoring the
        // wheel zoom at the pointer makes pinch feel centered on the fingers.
        ZoomAtPoint(delta > 0 ? _view.Zoom * 1.1 : _view.Zoom / 1.1, point.Position);
        e.Handled = true;
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (point.Properties.IsLeftButtonPressed ||
            e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _isPanning = true;
            _panPointerId = e.Pointer.PointerId;
            _lastPanPoint = point.Position;
            Viewport.CapturePointer(e.Pointer);
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _touchPoints[e.Pointer.PointerId] = point.Position;
            UpdatePinchState();
        }
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch &&
            _touchPoints.ContainsKey(e.Pointer.PointerId))
        {
            _touchPoints[e.Pointer.PointerId] = point.Position;
        }

        if (_isPinching)
        {
            // The focal point follows the fingers, so pan with it and scale
            // around it; pointer panning is suppressed while pinching.
            _lastPanPoint = point.Position;
            if (_imagePixelWidth == 0)
            {
                return;
            }

            if (!TryGetPinchGeometry(out var center, out var distance) ||
                distance < 1 || _pinchDistance < 1)
            {
                return;
            }

            _view.PanX += center.X - _pinchCenter.X;
            _view.PanY += center.Y - _pinchCenter.Y;
            ApplyViewTransform();
            ZoomAtPoint(_view.Zoom * (distance / _pinchDistance), center);
            _pinchCenter = center;
            _pinchDistance = distance;
            return;
        }

        if (!_isPanning || e.Pointer.PointerId != _panPointerId ||
            _imagePixelWidth == 0)
        {
            return;
        }

        var deltaX = point.Position.X - _lastPanPoint.X;
        var deltaY = point.Position.Y - _lastPanPoint.Y;
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        _view.PanX += deltaX;
        _view.PanY += deltaY;
        // The status line covers zoom, rotation and the frame number, none of
        // which a pan changes, so rewriting it on every move was pure work.
        ApplyViewTransform();
        _lastPanPoint = point.Position;
        e.Handled = true;
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning && e.Pointer.PointerId == _panPointerId)
        {
            _isPanning = false;
        }

        if (_touchPoints.Remove(e.Pointer.PointerId))
        {
            UpdatePinchState();
            if (_isPanning)
            {
                _lastPanPoint = e.GetCurrentPoint(Viewport).Position;
            }
        }
    }

    private void UpdatePinchState()
    {
        if (TryGetPinchGeometry(out var center, out var distance))
        {
            _isPinching = true;
            _pinchCenter = center;
            _pinchDistance = distance;
        }
        else
        {
            _isPinching = false;
            _pinchDistance = 0;
        }
    }

    /// <summary>
    /// Reports the midpoint and distance of the first two touch contacts.
    /// Extra contacts beyond two are ignored.
    /// </summary>
    private bool TryGetPinchGeometry(out Point center, out double distance)
    {
        center = default;
        distance = 0;
        if (_touchPoints.Count < 2)
        {
            return false;
        }

        var first = default(Point);
        var second = default(Point);
        var index = 0;
        foreach (var point in _touchPoints.Values)
        {
            if (index == 0)
            {
                first = point;
            }
            else
            {
                second = point;
                break;
            }

            index++;
        }

        center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        return true;
    }

    /// <summary>
    /// Scales the image around an anchor point so the content under the
    /// fingers or the cursor stays put. The image scales around its center
    /// and then pans, so the pan offset compensates for the difference the
    /// scale change introduces at the anchor, expressed in the pre-scale
    /// space (anchor - center - pan).
    /// </summary>
    private void ZoomAtPoint(double zoom, Point anchor)
    {
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _view.Zoom) < 0.0001)
        {
            return;
        }

        var deltaX = anchor.X - Viewport.ActualWidth / 2 - _view.PanX;
        var deltaY = anchor.Y - Viewport.ActualHeight / 2 - _view.PanY;
        var factor = 1 - newZoom / _view.Zoom;
        _view.PanX += factor * deltaX;
        _view.PanY += factor * deltaY;
        SetZoom(newZoom);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_view.Zoom / ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_view.Zoom * ZoomStep);
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(-90);
    }

    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(90);
    }

    /// <summary>
    /// Turns the view by a quarter turn. The angle stays inside [0, 360) so
    /// the readout never shows a negative or wrapped value.
    /// </summary>
    private void RotateBy(double degrees)
    {
        _view.Rotation = (_view.Rotation + degrees) % 360;
        if (_view.Rotation < 0)
        {
            _view.Rotation += 360;
        }

        ApplyViewTransform();
        UpdateViewStatus();
    }

    private void FlipHorizontalButton_Click(object sender, RoutedEventArgs e)
    {
        _view.FlipHorizontal = !_view.FlipHorizontal;
        ApplyViewTransform();
        UpdateViewStatus();
    }

    private void FlipVerticalButton_Click(object sender, RoutedEventArgs e)
    {
        _view.FlipVertical = !_view.FlipVertical;
        ApplyViewTransform();
        UpdateViewStatus();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyActualSize();
    }

    private void ApplyActualSize()
    {
        var ratio = FitToPixelRatio;
        if (ratio <= 0)
        {
            return;
        }

        // 1:1 must be exact even when the needed scale falls outside the
        // interactive wheel limits; those limits only bound wheel zooming.
        _view.Zoom = 1.0 / ratio;
        _view.PanX = 0;
        _view.PanY = 0;
        ApplyViewTransform();
        UpdateViewStatus();
        _ = UpgradeToFullResolutionAsync();
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

    private void SetZoom(double zoom)
    {
        _view.Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyViewTransform();
        UpdateViewStatus();

        if (_view.Zoom > 1.001)
        {
            // Zooming past fit magnifies a preview bitmap, so upgrade to the
            // real pixels once. The call is a no-op for full-resolution images.
            _ = UpgradeToFullResolutionAsync();
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

        if (Math.Abs(_view.Zoom - 1.0) < 0.001)
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

    private void ZoomInAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_view.Zoom * ZoomStep);
    }

    private void ZoomOutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_view.Zoom / ZoomStep);
    }

    private void ActualSizeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ApplyActualSize();
    }

    private void FitAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ResetView();
    }

    private void RotateLeftAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        RotateBy(-90);
    }

    private void RotateRightAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        RotateBy(90);
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
            TranslateTransform translate)
        {
            rotate.Angle = Rotation;
            scale.ScaleX = FlipHorizontal ? -Zoom : Zoom;
            scale.ScaleY = FlipVertical ? -Zoom : Zoom;
            translate.X = PanX;
            translate.Y = PanY;
        }
    }
}