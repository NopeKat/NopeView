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
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
        };

        // The zoom percentage is relative to the fit scale, which follows the
        // viewport, so the status line is rebuilt once the new layout lands.
        DispatcherQueue.TryEnqueue(UpdateViewStatus);
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
            _view.ApplyTo(ImageTransform);
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
        _view.ApplyTo(ImageTransform);
        UpdateViewStatus();
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

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        RotateClockwise();
    }

    private void RotateClockwise()
    {
        _view.Rotation = (_view.Rotation + 90) % 360;
        _view.ApplyTo(ImageTransform);
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
        _view.ApplyTo(ImageTransform);
        UpdateViewStatus();
        _ = UpgradeToFullResolutionAsync();
    }

    private double FitToPixelRatio
    {
        get
        {
            if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0 ||
                ImageView.ActualWidth <= 0 || ImageView.ActualHeight <= 0)
            {
                return 1.0;
            }

            // Stretch=Uniform fits the bitmap on its limiting axis. Using the
            // control width alone overestimates the fit scale for tall images,
            // which made 1:1 and the zoom percentage wrong.
            var fitScale = Math.Min(
                ImageView.ActualWidth / _imagePixelWidth,
                ImageView.ActualHeight / _imagePixelHeight);
            var rasterizationScale = Viewport.XamlRoot?.RasterizationScale ?? 1.0;
            return fitScale * rasterizationScale;
        }
    }

    private void SetZoom(double zoom)
    {
        _view.Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _view.ApplyTo(ImageTransform);
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
        _view.ApplyTo(ImageTransform);
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
        var panX = Math.Round(_view.PanX);
        var panY = Math.Round(_view.PanY);
        var pan = Math.Abs(panX) >= 1 || Math.Abs(panY) >= 1
            ? $" | pan {panX:0},{panY:0}"
            : "";
        var frame = _frameCount > 1
            ? $" | {_frameIndex + 1}/{_frameCount}"
            : "";

        if (Math.Abs(_view.Zoom - 1.0) < 0.001)
        {
            ViewStatusText.Text = $"Fit | {rotation:0} deg{frame}{pan}";
            return;
        }

        var percent = _view.Zoom * FitToPixelRatio * 100.0;
        ViewStatusText.Text = $"{Math.Round(percent):0}% | {rotation:0} deg{frame}{pan}";
    }

    private void SetViewControlsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RotateButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
        CopyImageMenuItem.IsEnabled = enabled;
        CopyPathMenuItem.IsEnabled = enabled;
        SaveAsMenuItem.IsEnabled = enabled;
        ShowInExplorerMenuItem.IsEnabled = enabled;
        FitMenuItem.IsEnabled = enabled;
        ActualSizeMenuItem.IsEnabled = enabled;
        CopyImageCommand.IsEnabled = enabled;
        CopyPathCommand.IsEnabled = enabled;
        SaveAsCommand.IsEnabled = enabled;
        ShowInExplorerCommand.IsEnabled = enabled;
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

    private void RotateAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        RotateClockwise();
    }

    /// <summary>
    /// Zoom, rotation and pan of the displayed image. Every gesture writes
    /// here and one ApplyTo call pushes the values into the render transform,
    /// so the numbers and the transform cannot drift apart.
    /// </summary>
    private sealed class ViewState
    {
        internal double Zoom = 1.0;
        internal double Rotation;
        internal double PanX;
        internal double PanY;

        internal void Reset()
        {
            Zoom = 1.0;
            Rotation = 0;
            PanX = 0;
            PanY = 0;
        }

        internal void ApplyTo(CompositeTransform transform)
        {
            transform.ScaleX = Zoom;
            transform.ScaleY = Zoom;
            transform.Rotation = Rotation;
            transform.TranslateX = PanX;
            transform.TranslateY = PanY;
        }
    }
}