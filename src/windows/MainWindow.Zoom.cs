using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace NopeView.App;

// Zoom and view commands: scaling, rotation, flips, fit, and actual size.
public sealed partial class MainWindow : Window
{
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
        ScheduleFullResolutionUpgrade();
    }

    private void SetZoom(double zoom)
    {
        _view.Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyViewTransform();
        UpdateViewStatus();

        if (_view.Zoom > 1.001)
        {
            // Zooming past fit magnifies a preview bitmap, so the real pixels
            // replace it once the input stops.
            ScheduleFullResolutionUpgrade();
        }
    }

    /// <summary>
    /// Queues the full-resolution replacement for a magnified preview. The
    /// decode ends with one large bitmap upload, so it waits for the zoom input
    /// to stop instead of landing between two wheel ticks.
    /// </summary>
    private void ScheduleFullResolutionUpgrade()
    {
        if (!_previewActive || _frameCount > 1 || _directory is null)
        {
            return;
        }

        _upgradeTimer.Stop();
        _upgradeTimer.Start();
    }

    private void UpgradeTimer_Tick(object? sender, object e)
    {
        _upgradeTimer.Stop();
        if (_view.Zoom <= 1.001)
        {
            return;
        }

        _ = UpgradeToFullResolutionAsync();
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
}
