using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace NopeView.App;

// Pointer input: wheel zoom, pointer panning, and touch pinch tracking.
public sealed partial class MainWindow : Window
{
    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var clock = UiPerf.Start();
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
        UiPerf.Stop(clock, "zoom-wheel", UiPerf.Number(_view.Zoom));
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
        var clock = UiPerf.Start();
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
            UiPerf.Stop(clock, "pinch-move", UiPerf.Number(_view.Zoom));
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
        UiPerf.Stop(clock, "pan-move", UiPerf.Number(_view.Zoom));
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
}
