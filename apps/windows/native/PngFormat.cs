using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>Mirror of nv_format_image in nv_format_abi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvFormatImage
{
    public uint StructSize;
    public int Width;
    public int Height;
    public int Stride;
    public int PixelFormat;
    public IntPtr Pixels;
    public nuint PixelsSize;
}

/// <summary>Mirror of nv_format_info in nv_format_abi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvFormatInfo
{
    public uint StructSize;
    public IntPtr Name;
    public IntPtr FormatId;
    public IntPtr Extensions;
    public uint Capabilities;
}

/// <summary>Mirror of nv_format_decode_request in nv_format_abi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvFormatDecodeRequest
{
    public uint StructSize;
    public IntPtr Data;
    public nuint DataSize;
    public IntPtr Path;
    public int DesiredPixelFormat;
    public uint FrameIndex;
}

/// <summary>
/// Temporary direct binding to the PNG format library.
/// Uses the format ABI (nv_format_abi.h); the router will replace this later.
/// </summary>
internal static partial class PngFormat
{
    private const string LibraryName = "png_codec";
    private const int Ok = 0;
    private const int PixelFormatRgba8 = 1;

    static PngFormat()
    {
        NativeLibrary.SetDllImportResolver(typeof(PngFormat).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return IntPtr.Zero;
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "native", "x64");
        var candidate = Path.Combine(dir, "png_codec.dll");
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }

    // Returns the ABI version as an x.y.z.n string. The pointer refers to a
    // static string owned by the library, so it must not be freed here.
    [LibraryImport(LibraryName, EntryPoint = "nv_format_abi_version")]
    private static partial IntPtr GetVersionPtr();

    internal static string Version()
    {
        return Marshal.PtrToStringUTF8(GetVersionPtr()) ?? "0.0.0.0";
    }

    [LibraryImport(LibraryName, EntryPoint = "nv_format_query")]
    internal static partial int Query(ref NvFormatInfo info);

    [LibraryImport(LibraryName, EntryPoint = "nv_format_decode")]
    internal static partial int Decode(ref NvFormatDecodeRequest request, ref NvFormatImage image);

    [LibraryImport(LibraryName, EntryPoint = "nv_format_image_free")]
    internal static partial void ImageFree(ref NvFormatImage image);

    /// <summary>Decodes a file into RGBA bytes. The caller must release native memory via ImageFree.</summary>
    internal static bool TryDecodeFile(string path, out NvFormatImage image)
    {
        image = default;

        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var request = new NvFormatDecodeRequest
            {
                StructSize = (uint)Marshal.SizeOf<NvFormatDecodeRequest>(),
                Data = IntPtr.Zero,
                DataSize = 0,
                Path = pathPtr,
                DesiredPixelFormat = PixelFormatRgba8,
                FrameIndex = 0
            };
            image.StructSize = (uint)Marshal.SizeOf<NvFormatImage>();

            var status = Decode(ref request, ref image);
            return status == Ok;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>Reads the format's declared capabilities.</summary>
    internal static bool TryQuery(out NvFormatInfo info)
    {
        info = default;
        info.StructSize = (uint)Marshal.SizeOf<NvFormatInfo>();
        return Query(ref info) == Ok;
    }
}
