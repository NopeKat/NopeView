using System.Reflection;
using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Loads the native DLLs staged under Native/x64. Only one resolver can be
/// registered per assembly, so every binding goes through this gate.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static bool s_registered;

    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (s_registered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
            s_registered = true;
        }
    }

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName is not ("router" or "files"))
        {
            return IntPtr.Zero;
        }

        var candidate = Path.Combine(
            AppContext.BaseDirectory, "Native", "x64", libraryName + ".dll");
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }
}
