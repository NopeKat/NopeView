// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

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

    // The fit factor already baked into the frame's local size. A live resize
    // changes the scale against this value instead of relaying out the bitmap,
    // because a size change here repaints a full-resolution image on every
    // size event.
    private double _frameFit;

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

        var width = _imagePixelWidth * fit;
        var height = _imagePixelHeight * fit;
        if (ImageFrame.Width != width || ImageFrame.Height != height)
        {
            ImageFrame.Width = width;
            ImageFrame.Height = height;
        }

        if (ImageFrame.Visibility != Visibility.Visible)
        {
            ImageFrame.Visibility = Visibility.Visible;
        }

        _frameFit = fit;
        ApplyViewTransform();
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
        ApplyViewTransform();
    }

    /// <summary>
    /// A stroke lives in the image's local space and would grow with zoom.
    /// Scaling it by the inverse keeps its on-screen weight constant.
    /// </summary>
    private void UpdateImageFrameStroke(double scale)
    {
        ImageFrameStroke.StrokeThickness = 1.0 / Math.Max(0.1, scale);
    }

    /// <summary>
    /// Pushes the current view into the frame and refreshes metrics that
    /// depend on the resulting scale. The fit factor that the frame's local
    /// size does not carry yet is folded into the scale, so a viewport resize
    /// only rewrites transform values.
    /// </summary>
    private void ApplyViewTransform()
    {
        var clock = UiPerf.Start();
        var fit = GetFitScale(_imageFrameStyle);
        var compensation = _frameFit > 0 && fit > 0 ? fit / _frameFit : 1.0;
        _view.ApplyTo(ImageRotation, ImageScale, ImageTranslation, compensation);
        UpdateImageFrameStroke(_view.Zoom * compensation);
        UiPerf.Stop(clock, "apply-transform", UiPerf.Number(_view.Zoom));
    }
}
