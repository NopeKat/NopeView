using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>Mirror of nv_router_image in nv_router_abi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvRouterImage
{
    public uint StructSize;
    public int Width;
    public int Height;
    public int Stride;
    public int PixelFormat;
    public IntPtr Pixels;
    public nuint PixelsSize;
    public uint FrameCount;
    public uint FrameDelayMs;
    public int SourceWidth;
    public int SourceHeight;
}

/// <summary>Mirror of nv_router_format in nv_router_abi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvRouterFormat
{
    public uint StructSize;
    public IntPtr FormatId;
    public IntPtr Extensions;
    public uint CanDecode;
    public uint CanEncode;
    public uint CanAnimate;
}

/// <summary>One format discovered by the router.</summary>
internal readonly record struct RouterFormatInfo(
    string Id,
    string Extensions,
    bool CanDecode,
    bool CanEncode,
    bool CanAnimate);

/// <summary>
/// Binding to the codec router (nv_router_abi.h).
/// The app never references a concrete format library; it only knows the router.
/// </summary>
internal static partial class RouterApi
{
    private const string LibraryName = "router";
    private const int Ok = 0;
    private const int ErrNotFound = -2;
    internal const int ErrUnsupportedFormat = -3;
    internal const int ErrDecodeFailed = -4;
    internal const int ErrOutOfMemory = -5;
    internal const int ErrIo = -6;
    internal const int ErrAbiMismatch = -7;
    internal const int ErrEncodeFailed = -8;
    internal const int PixelFormatRgba8 = 1;
    internal const int PixelFormatBgra8 = 2;

    static RouterApi()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    // Returns the ABI version as an x.y.z.n string. The pointer refers to a
    // static string owned by the router, so it must not be freed here.
    [LibraryImport(LibraryName, EntryPoint = "nv_router_abi_version")]
    private static partial IntPtr GetVersionPtr();

    [LibraryImport(LibraryName, EntryPoint = "nv_router_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Create(string? codecDirectory, out IntPtr context);

    [LibraryImport(LibraryName, EntryPoint = "nv_router_destroy")]
    private static partial void Destroy(IntPtr context);

    [LibraryImport(LibraryName, EntryPoint = "nv_router_enum_formats")]
    private static partial int EnumFormats(IntPtr context, uint index, ref NvRouterFormat format);

    [LibraryImport(LibraryName, EntryPoint = "nv_router_decode_file_ex", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DecodeFileEx(IntPtr context, string path, int desiredPixelFormat, uint frameIndex, int maxDimension, ref NvRouterImage image);

    [LibraryImport(LibraryName, EntryPoint = "nv_router_image_free")]
    private static partial void ImageFree(ref NvRouterImage image);

    [LibraryImport(LibraryName, EntryPoint = "nv_router_encode_file", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int EncodeFile(IntPtr context, string path, ref NvRouterImage image, int quality);

    internal static string Version()
    {
        return Marshal.PtrToStringUTF8(GetVersionPtr()) ?? "0.0.0.0";
    }

    /// <summary>Creates a router context. A null directory uses the router's own folder.</summary>
    internal static bool TryCreate(string? codecDirectory, out IntPtr context)
    {
        return Create(codecDirectory, out context) == Ok;
    }

    internal static void Close(IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            Destroy(context);
        }
    }

    /// <summary>Lists the formats the router discovered.</summary>
    internal static IReadOnlyList<RouterFormatInfo> ListFormats(IntPtr context)
    {
        var formats = new List<RouterFormatInfo>();

        for (uint index = 0; ; index++)
        {
            var format = default(NvRouterFormat);
            format.StructSize = (uint)Marshal.SizeOf<NvRouterFormat>();

            if (EnumFormats(context, index, ref format) != Ok)
            {
                break;
            }

            formats.Add(new RouterFormatInfo(
                Marshal.PtrToStringUTF8(format.FormatId) ?? "?",
                Marshal.PtrToStringUTF8(format.Extensions) ?? "",
                format.CanDecode != 0u,
                format.CanEncode != 0u,
                format.CanAnimate != 0u));
        }

        return formats;
    }

    /// <summary>
    /// Decodes one frame through the router. Release native memory with
    /// ImageFree. Defaults keep the original single-image RGBA behavior.
    /// A positive maxDimension caps the long edge of the returned pixels; the
    /// image still reports the original size in SourceWidth and SourceHeight.
    /// </summary>
    internal static bool TryDecodeFile(
        IntPtr context,
        string path,
        out NvRouterImage image,
        out int status,
        uint frameIndex = 0u,
        int pixelFormat = PixelFormatRgba8,
        int maxDimension = 0)
    {
        image = default;
        image.StructSize = (uint)Marshal.SizeOf<NvRouterImage>();

        status = DecodeFileEx(context, path, pixelFormat, frameIndex, maxDimension, ref image);
        return status == Ok;
    }

    internal static void FreeImage(ref NvRouterImage image)
    {
        ImageFree(ref image);
    }

    /// <summary>
    /// Encodes an image to a file. The format comes from the file extension.
    /// A quality of 0 selects the format default.
    /// </summary>
    internal static int EncodeToFile(
        IntPtr context,
        string path,
        ref NvRouterImage image,
        int quality = 0)
    {
        return EncodeFile(context, path, ref image, quality);
    }

    internal static bool IsNotFound(int status)
    {
        return status == ErrNotFound;
    }
}
