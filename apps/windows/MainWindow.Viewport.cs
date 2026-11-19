using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    }

    private void PanThumb_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(PanThumb);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // Precision touchpad pinch arrives as Ctrl+wheel, so anchoring the
        // wheel zoom at the pointer makes pinch feel centered on the fingers.
        ZoomAtPoint(delta > 0 ? _zoom * 1.1 : _zoom / 1.1, point.Position);
        e.Handled = true;
    }

    private void PanThumb_TouchPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            return;
        }

        _touchPoints[e.Pointer.PointerId] = e.GetCurrentPoint(PanThumb).Position;
        UpdatePinchState();
    }

    private void PanThumb_TouchMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch ||
            !_touchPoints.ContainsKey(e.Pointer.PointerId))
        {
            return;
        }

        _touchPoints[e.Pointer.PointerId] = e.GetCurrentPoint(PanThumb).Position;
        if (!_isPinching || _imagePixelWidth == 0)
        {
            return;
        }

        if (!TryGetPinchGeometry(out var center, out var distance) ||
            distance < 1 || _pinchDistance < 1)
        {
            return;
        }

        // The focal point follows the fingers, so pan with it and scale
        // around it; the thumb drag is suppressed while pinching.
        ImageTransform.TranslateX += center.X - _pinchCenter.X;
        ImageTransform.TranslateY += center.Y - _pinchCenter.Y;
        ZoomAtPoint(_zoom * (distance / _pinchDistance), center);
        _pinchCenter = center;
        _pinchDistance = distance;
    }

    private void PanThumb_TouchReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_touchPoints.Remove(e.Pointer.PointerId))
        {
            UpdatePinchState();
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
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        var deltaX = anchor.X - PanThumb.ActualWidth / 2 - ImageTransform.TranslateX;
        var deltaY = anchor.Y - PanThumb.ActualHeight / 2 - ImageTransform.TranslateY;
        var factor = 1 - newZoom / _zoom;
        ImageTransform.TranslateX += factor * deltaX;
        ImageTransform.TranslateY += factor * deltaY;
        SetZoom(newZoom);
    }

    private void PanThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_imagePixelWidth == 0 || _isPinching)
        {
            return;
        }

        ImageTransform.TranslateX += e.HorizontalChange;
        ImageTransform.TranslateY += e.VerticalChange;
        UpdateViewStatus();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom / ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom * ZoomStep);
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        RotateClockwise();
    }

    private void RotateClockwise()
    {
        _rotation = (_rotation + 90) % 360;
        ImageTransform.Rotation = _rotation;
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
        _zoom = 1.0 / ratio;
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
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
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        UpdateViewStatus();

        if (_zoom > 1.001)
        {
            // Zooming past fit magnifies a preview bitmap, so upgrade to the
            // real pixels once. The call is a no-op for full-resolution images.
            _ = UpgradeToFullResolutionAsync();
        }
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _rotation = 0;
        ImageTransform.ScaleX = 1.0;
        ImageTransform.ScaleY = 1.0;
        ImageTransform.Rotation = 0;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        var rotation = Math.Round(_rotation);
        var panX = Math.Round(ImageTransform.TranslateX);
        var panY = Math.Round(ImageTransform.TranslateY);
        var pan = Math.Abs(panX) >= 1 || Math.Abs(panY) >= 1
            ? $" | pan {panX:0},{panY:0}"
            : "";
        var frame = _frameCount > 1
            ? $" | {_frameIndex + 1}/{_frameCount}"
            : "";

        if (Math.Abs(_zoom - 1.0) < 0.001)
        {
            ViewStatusText.Text = $"Fit | {rotation:0} deg{frame}{pan}";
            return;
        }

        var percent = _zoom * FitToPixelRatio * 100.0;
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
        SetZoom(_zoom * ZoomStep);
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
        SetZoom(_zoom / ZoomStep);
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
}
