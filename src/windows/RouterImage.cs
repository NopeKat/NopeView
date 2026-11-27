// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using NopeView.App.Native;
using Windows.Graphics.Imaging;
using WinRT;

namespace NopeView.App;

/// <summary>
/// One decoded router image. Disposing releases the native pixels, so callers
/// can use a using declaration instead of repeating the try/finally pair.
/// </summary>
internal sealed class RouterImage : IDisposable
{
    private NvRouterImage _image;
    private bool _owned = true;

    private RouterImage(NvRouterImage image)
    {
        _image = image;
    }

    internal NvRouterImage Value => _image;

    /// <summary>
    /// Decodes one frame through the router. Returns null when the router
    /// rejects the request; status carries the reason either way.
    /// </summary>
    internal static RouterImage? Decode(
        IntPtr context,
        string path,
        uint frameIndex,
        int pixelFormat,
        int maxDimension,
        out int status)
    {
        if (!RouterApi.TryDecodeFile(
                context, path, out var image, out status, frameIndex, pixelFormat, maxDimension))
        {
            return null;
        }

        return new RouterImage(image);
    }

    public void Dispose()
    {
        if (_owned)
        {
            RouterApi.FreeImage(ref _image);
            _owned = false;
        }
    }
}

/// <summary>
/// Copies router pixels into a SoftwareBitmap. Display surfaces need
/// premultiplied alpha; the clipboard and encoders need straight alpha, and
/// only the premultiplied path pays for the premultiply work. An EXIF
/// orientation is applied while copying, so a later rotation is not needed and
/// the frame reports the dimensions the camera meant.
/// </summary>
internal static unsafe class PixelBuffer
{
    internal static SoftwareBitmap ToSoftwareBitmap(
        NvRouterImage image,
        BitmapAlphaMode alphaMode,
        int orientation = ExifOrientation.Normal)
    {
        var sourceWidth = image.Width;
        var sourceHeight = image.Height;
        var swapAxes = ExifOrientation.SwapsAxes(orientation);
        var width = swapAxes ? sourceHeight : sourceWidth;
        var height = swapAxes ? sourceWidth : sourceHeight;
        var rowBytes = width * 4;
        var swapRedBlue = image.PixelFormat != RouterApi.PixelFormatBgra8;
        var premultiply = alphaMode == BitmapAlphaMode.Premultiplied;
        var opaque = premultiply && IsOpaque((byte*)image.Pixels, checked(width * height * 4));

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, width, height, alphaMode);

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        var description = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        if (reference.As<IMemoryBufferByteAccess>().GetBuffer(out var destination, out _) < 0)
        {
            throw new InvalidOperationException("The image buffer could not be accessed.");
        }

        for (var y = 0; y < height; y++)
        {
            var target = (byte*)destination
                + description.StartIndex
                + (long)y * description.Stride;

            if (orientation == ExifOrientation.Normal &&
                !swapRedBlue && (!premultiply || opaque))
            {
                // Nothing to reorder and no transparency to fold in.
                var source = (byte*)image.Pixels + (long)y * image.Stride;
                System.Buffer.MemoryCopy(source, target, rowBytes, rowBytes);
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                int sx;
                int sy;
                SourcePoint(orientation, x, y, sourceWidth, sourceHeight, out sx, out sy);

                var pixel = (byte*)image.Pixels + (long)sy * image.Stride + (long)sx * 4;
                var blue = pixel[0];
                var green = pixel[1];
                var red = pixel[2];
                var alpha = pixel[3];

                if (swapRedBlue)
                {
                    (red, blue) = (blue, red);
                }

                if (premultiply && !opaque)
                {
                    blue = (byte)(blue * alpha / 255);
                    green = (byte)(green * alpha / 255);
                    red = (byte)(red * alpha / 255);
                }

                var offset = x * 4;
                target[offset] = blue;
                target[offset + 1] = green;
                target[offset + 2] = red;
                target[offset + 3] = alpha;
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Source pixel behind one display pixel, for each of the eight EXIF
    /// orientations. The display size already carries the axis swap, so a
    /// quarter turn reads the source along the other axis.
    /// </summary>
    private static void SourcePoint(
        int orientation,
        int x,
        int y,
        int sourceWidth,
        int sourceHeight,
        out int sx,
        out int sy)
    {
        switch (orientation)
        {
            case ExifOrientation.FlipHorizontal:
                sx = sourceWidth - 1 - x;
                sy = y;
                break;
            case ExifOrientation.Rotate180:
                sx = sourceWidth - 1 - x;
                sy = sourceHeight - 1 - y;
                break;
            case ExifOrientation.FlipVertical:
                sx = x;
                sy = sourceHeight - 1 - y;
                break;
            case ExifOrientation.Transpose:
                sx = y;
                sy = x;
                break;
            case ExifOrientation.Rotate90:
                sx = y;
                sy = sourceHeight - 1 - x;
                break;
            case ExifOrientation.Transverse:
                sx = sourceWidth - 1 - y;
                sy = sourceHeight - 1 - x;
                break;
            case ExifOrientation.Rotate270:
                sx = sourceWidth - 1 - y;
                sy = x;
                break;
            default:
                sx = x;
                sy = y;
                break;
        }
    }

    /// <summary>
    /// Copies the pixels into a new buffer turned the way the EXIF orientation
    /// says, for paths that hand the pixels to an encoder rather than to a
    /// bitmap. The caller owns the buffer and frees it with FreeHGlobal, which
    /// is why the returned image carries no release callback.
    /// </summary>
    internal static NvRouterImage Turn(NvRouterImage source, int orientation)
    {
        var swapAxes = ExifOrientation.SwapsAxes(orientation);
        var width = swapAxes ? source.Height : source.Width;
        var height = swapAxes ? source.Width : source.Height;
        var rowBytes = checked(width * 4);
        var size = checked((long)rowBytes * height);
        var pixels = Marshal.AllocHGlobal((nint)size);

        try
        {
            var target = (byte*)pixels;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int sx;
                    int sy;
                    SourcePoint(orientation, x, y, source.Width, source.Height, out sx, out sy);

                    var from = (byte*)source.Pixels + (long)sy * source.Stride + (long)sx * 4;
                    var to = target + (long)y * rowBytes + (long)x * 4;
                    to[0] = from[0];
                    to[1] = from[1];
                    to[2] = from[2];
                    to[3] = from[3];
                }
            }
        }
        catch
        {
            Marshal.FreeHGlobal(pixels);
            throw;
        }

        return new NvRouterImage
        {
            StructSize = source.StructSize,
            Width = width,
            Height = height,
            Stride = rowBytes,
            PixelFormat = source.PixelFormat,
            Pixels = pixels,
            PixelsSize = (nuint)size,
            FrameCount = source.FrameCount,
            FrameDelayMs = source.FrameDelayMs,
            SourceWidth = swapAxes ? source.SourceHeight : source.SourceWidth,
            SourceHeight = swapAxes ? source.SourceWidth : source.SourceHeight,
            // The buffer belongs to the managed side, so the codec must never
            // release it.
            PixelsRelease = IntPtr.Zero
        };
    }

    /// <summary>
    /// True when every pixel is fully opaque, which makes premultiplication a
    /// no-op and removes a multiply per channel from the common path.
    /// </summary>
    private static unsafe bool IsOpaque(byte* pixels, int byteCount)
    {
        for (var index = 3; index < byteCount; index += 4)
        {
            if (pixels[index] != 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gives direct access to the bytes of an IMemoryBufferReference. WinRT
    /// exposes the pointer through this non-WinRT interface, which lets pixels
    /// be written into a bitmap without an intermediate buffer.
    /// </summary>
    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        [PreserveSig]
        int GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
