// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace NopeView.App;

// Zoom and view commands: scaling, rotation, flips, fit, and actual size.
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Interactive zoom ceiling. It never falls below the zoom already in use
    /// because 1:1 can need more than the wheel allows, and a ceiling under the
    /// current value would turn a zoom-in step into a jump back down.
    /// </summary>
    private double ZoomCeiling => Math.Max(MaxZoom, _view.Zoom);

    /// <summary>
    /// Scales the image around an anchor point so the content under the
    /// fingers or the cursor stays put. The image scales around its center
    /// and then pans, so the pan offset compensates for the difference the
    /// scale change introduces at the anchor, expressed in the pre-scale
    /// space (anchor - center - pan).
    /// </summary>
    private void ZoomAtPoint(double zoom, Point anchor)
    {
        var newZoom = Math.Clamp(zoom, MinZoom, ZoomCeiling);
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
    /// the readout never shows a negative or wrapped value. A quarter turn
    /// swaps the bitmap's extents on screen, so the fitted box changes and the
    /// frame is re-sized to the new fit instead of being clipped by the old one.
    /// </summary>
    private void RotateBy(double degrees)
    {
        var wasFit = IsAtFit;
        var oldFit = GetFitScale(_imageFrameStyle);

        _view.Rotation = (_view.Rotation + degrees) % 360;
        if (_view.Rotation < 0)
        {
            _view.Rotation += 360;
        }

        // The new fit is known before the frame is re-sized, so a view that was
        // magnified keeps the same on-screen scale with one transform pass.
        var newFit = GetFitScale(_imageFrameStyle);
        if (!wasFit && oldFit > 0 && newFit > 0)
        {
            _view.Zoom = Math.Clamp(
                _view.Zoom * oldFit / newFit, MinZoom, ZoomCeiling);
        }

        UpdateImageFrameSize();
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
        _view.Zoom = Math.Clamp(zoom, MinZoom, ZoomCeiling);
        ApplyViewTransform();
        UpdateViewStatus();

        if (IsMagnified)
        {
            // Magnifying a preview bitmap is the one case that needs the real
            // pixels, and they arrive once the input stops.
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
        if (UpgradeSource is null)
        {
            return;
        }

        _upgradeTimer.Stop();
        _upgradeTimer.Start();
    }

    private void UpgradeTimer_Tick(object? sender, object e)
    {
        _upgradeTimer.Stop();
        if (!IsMagnified)
        {
            return;
        }

        // The loading ring is the existing "an open is decoding" signal (the
        // settings prewarm reads it too). A full decode that landed here would
        // take the generation away from the open and leave the ring running
        // over a stale image.
        if (LoadingRing.Visibility == Visibility.Visible)
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
