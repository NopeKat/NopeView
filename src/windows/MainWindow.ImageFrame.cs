using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;

namespace NopeView.App;

// Image frame: keeps the optional edge treatment glued to the bitmap bounds
// while the viewport keeps owning gestures and clipping.
public sealed partial class MainWindow : Window
{
    // Shadows need room inside the viewport clip. At fit the frame is inset
    // by this much on every side, so the soft edge is not cut off.
    private const double ImageShadowInset = 16;

    private readonly ThemeShadow _imageShadow = new();
    private ImageFrameStyle _imageFrameStyle;

    /// <summary>
    /// Sizes the frame to the fitted bitmap. The transform still runs on top
    /// of this size, so zoom, pan and rotation apply to the visible image
    /// rather than to the whole viewport.
    /// </summary>
    private void UpdateImageFrameSize()
    {
        if (_imagePixelWidth <= 0 ||
            _imagePixelHeight <= 0 ||
            Viewport.ActualWidth <= 0 ||
            Viewport.ActualHeight <= 0)
        {
            ImageFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var fit = GetFitScale(_imageFrameStyle);
        if (fit <= 0)
        {
            ImageFrame.Visibility = Visibility.Collapsed;
            return;
        }

        ImageFrame.Width = _imagePixelWidth * fit;
        ImageFrame.Height = _imagePixelHeight * fit;
        ImageFrame.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Device-independent units per image pixel when the bitmap is fitted.
    /// Shadow mode reserves an inset so the soft edge can render inside the
    /// viewport clip. Returns zero while the layout is not measurable yet.
    /// </summary>
    private double GetFitScale(ImageFrameStyle style)
    {
        if (_imagePixelWidth <= 0 ||
            _imagePixelHeight <= 0 ||
            Viewport.ActualWidth <= 0 ||
            Viewport.ActualHeight <= 0)
        {
            return 0;
        }

        var inset = style == ImageFrameStyle.Shadow ? ImageShadowInset : 0;
        var availableWidth = Math.Max(1, Viewport.ActualWidth - inset * 2);
        var availableHeight = Math.Max(1, Viewport.ActualHeight - inset * 2);
        return Math.Min(
            availableWidth / _imagePixelWidth,
            availableHeight / _imagePixelHeight);
    }

    /// <summary>
    /// Selects the edge treatment without rebuilding the image or its source.
    /// </summary>
    private void ApplyImageDecoration()
    {
        var shadowed = _imageFrameStyle == ImageFrameStyle.Shadow;
        ImageFrame.Shadow = shadowed ? _imageShadow : null;
        ImageFrame.Translation = shadowed
            ? new Vector3(0, 0, 24)
            : Vector3.Zero;
        ImageFrameStroke.Visibility =
            _imageFrameStyle == ImageFrameStyle.Stroke
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateImageFrameStroke();
    }

    /// <summary>
    /// A stroke lives in the image's local space and would grow with zoom.
    /// Scaling it by the inverse keeps its on-screen weight constant.
    /// </summary>
    private void UpdateImageFrameStroke()
    {
        ImageFrameStroke.StrokeThickness = 1.0 / Math.Max(0.1, _view.Zoom);
    }

    /// <summary>
    /// Pushes the current view into the frame and refreshes metrics that
    /// depend on the resulting scale.
    /// </summary>
    private void ApplyViewTransform()
    {
        _view.ApplyTo(ImageRotation, ImageScale, ImageTranslation);
        UpdateImageFrameStroke();
    }
}
