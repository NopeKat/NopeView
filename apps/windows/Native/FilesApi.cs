using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Binding to the shared file list service (nv_files_abi.h). The core owns the
/// directory walk, extension filter, and sorting; the app only consumes paths.
/// </summary>
internal static partial class FilesApi
{
    private const string LibraryName = "files";
    private const int Ok = 0;

    static FilesApi()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    [LibraryImport(LibraryName, EntryPoint = "nv_files_abi_version")]
    private static partial IntPtr GetVersionPtr();

    [LibraryImport(LibraryName, EntryPoint = "nv_file_list_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Create(string directory, string extensions, out IntPtr list);

    [LibraryImport(LibraryName, EntryPoint = "nv_file_list_destroy")]
    private static partial void Destroy(IntPtr list);

    [LibraryImport(LibraryName, EntryPoint = "nv_file_list_count")]
    private static partial uint Count(IntPtr list);

    [LibraryImport(LibraryName, EntryPoint = "nv_file_list_path")]
    private static partial IntPtr PathAt(IntPtr list, uint index);

    [LibraryImport(LibraryName, EntryPoint = "nv_file_list_index_of", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int IndexOf(IntPtr list, string path);

    internal static string Version()
    {
        return Marshal.PtrToStringUTF8(GetVersionPtr()) ?? "0.0.0.0";
    }

    internal static bool TryListPaths(
        string directory,
        IEnumerable<string> extensions,
        out IReadOnlyList<string> paths)
    {
        paths = Array.Empty<string>();
        var filter = string.Join(',', extensions);
        int status;
        IntPtr list;

        try
        {
            status = Create(directory, filter, out list);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }

        if (status != Ok || list == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var count = Count(list);
            var result = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var pointer = PathAt(list, index);
                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                var path = Marshal.PtrToStringUTF8(pointer);
                if (!string.IsNullOrEmpty(path))
                {
                    result.Add(path);
                }
            }

            paths = result;
            return true;
        }
        finally
        {
            Destroy(list);
        }
    }
}
