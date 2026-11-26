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
/// only the premultiplied path pays for the premultiply work.
/// </summary>
internal static unsafe class PixelBuffer
{
    internal static SoftwareBitmap ToSoftwareBitmap(NvRouterImage image, BitmapAlphaMode alphaMode)
    {
        var width = image.Width;
        var height = image.Height;
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
            var source = (byte*)image.Pixels + (long)y * image.Stride;
            var target = (byte*)destination
                + description.StartIndex
                + (long)y * description.Stride;

            if (!swapRedBlue && (!premultiply || opaque))
            {
                // Nothing to reorder and no transparency to fold in.
                System.Buffer.MemoryCopy(source, target, rowBytes, rowBytes);
                continue;
            }

            for (var x = 0; x < rowBytes; x += 4)
            {
                var blue = source[x];
                var green = source[x + 1];
                var red = source[x + 2];
                var alpha = source[x + 3];

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

                target[x] = blue;
                target[x + 1] = green;
                target[x + 2] = red;
                target[x + 3] = alpha;
            }
        }

        return bitmap;
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
