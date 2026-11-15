using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>Mirror of the C ABI struct; field order matches nv_image in png_codec.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvImage
{
    public int Width;
    public int Height;
    public int Channels;
    public IntPtr Pixels;
}

internal static partial class PngCodec
{
    private const string LibraryName = "png_codec";
    private const int Ok = 0;

    static PngCodec()
    {
        NativeLibrary.SetDllImportResolver(typeof(PngCodec).Assembly, Resolve);
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
    // static string owned by the codec, so it must not be freed here.
    [LibraryImport(LibraryName, EntryPoint = "nv_png_version")]
    private static partial IntPtr GetVersionPtr();

    internal static string Version()
    {
        return Marshal.PtrToStringUTF8(GetVersionPtr()) ?? "0.0.0.0";
    }

    [LibraryImport(LibraryName, EntryPoint = "nv_png_decode_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DecodeFile(string path, out NvImage image);

    [LibraryImport(LibraryName, EntryPoint = "nv_png_image_free")]
    internal static partial void ImageFree(ref NvImage image);

    /// <summary>Decodes a PNG into RGBA bytes. The caller must release native memory via ImageFree.</summary>
    internal static bool TryDecode(string path, out NvImage image)
    {
        var status = DecodeFile(path, out image);
        return status == Ok;
    }
}
