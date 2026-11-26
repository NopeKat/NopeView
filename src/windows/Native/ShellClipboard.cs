// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Clipboard helpers that the WinRT clipboard does not cover. A file is put
/// on the clipboard as a shell file list, which is the format Explorer
/// writes when it copies a file, so pasting elsewhere copies the file itself
/// instead of its pixels.
/// </summary>
internal static class ShellClipboard
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int DROPEFFECT_COPY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        // Offset of the file list from the start of this structure.
        internal uint Offset;
        internal int X;
        internal int Y;
        // Nonzero when the structure describes a drag image rather than a list.
        internal int NonClient;
        // Nonzero when the names are UTF-16 rather than ANSI.
        internal int Wide;
    }

    /// <summary>
    /// Replaces the clipboard with a single file. The window handle only
    /// names the clipboard owner.
    /// </summary>
    internal static void SetFile(nint hWnd, string path)
    {
        var effectFormat = NativeMethods.RegisterClipboardFormat("Preferred DropEffect");
        if (effectFormat == 0)
        {
            throw new InvalidOperationException(
                "The drop effect format could not be registered.");
        }

        var fileList = AllocateFileDrop(path);
        var copyEffect = AllocateCopyEffect();

        if (!NativeMethods.OpenClipboard(hWnd))
        {
            NativeMethods.GlobalFree(fileList);
            NativeMethods.GlobalFree(copyEffect);
            throw new InvalidOperationException(
                $"The clipboard could not be opened (error {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                throw new InvalidOperationException(
                    $"The clipboard could not be cleared (error {Marshal.GetLastPInvokeError()}).");
            }

            // Memory that the clipboard accepts belongs to the system from
            // here on and must not be freed by this process any more.
            if (NativeMethods.SetClipboardData(CF_HDROP, fileList) == 0)
            {
                throw new InvalidOperationException(
                    $"The clipboard rejected the file list (error {Marshal.GetLastPInvokeError()}).");
            }
            fileList = 0;

            if (NativeMethods.SetClipboardData(effectFormat, copyEffect) == 0)
            {
                throw new InvalidOperationException(
                    $"The clipboard rejected the drop effect (error {Marshal.GetLastPInvokeError()}).");
            }
            copyEffect = 0;
        }
        finally
        {
            if (fileList != 0)
            {
                NativeMethods.GlobalFree(fileList);
            }

            if (copyEffect != 0)
            {
                NativeMethods.GlobalFree(copyEffect);
            }

            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Builds a DROPFILES block holding one UTF-16 path and the two null
    /// characters that terminate the list.
    /// </summary>
    private static nint AllocateFileDrop(string path)
    {
        var header = Marshal.SizeOf<DropFiles>();
        var characters = new char[path.Length + 2];
        path.CopyTo(characters);
        var bytes = header + characters.Length * sizeof(char);

        var memory = Allocate(bytes, "file list");
        var locked = Lock(memory, "file list");
        var block = new DropFiles { Offset = (uint)header, Wide = 1 };
        Marshal.StructureToPtr(block, locked, false);
        Marshal.Copy(characters, 0, locked + header, characters.Length);
        NativeMethods.GlobalUnlock(memory);
        return memory;
    }

    /// <summary>
    /// Marks the operation as a copy, so pasting the file moves nothing.
    /// </summary>
    private static nint AllocateCopyEffect()
    {
        var memory = Allocate(sizeof(int), "drop effect");
        var locked = Lock(memory, "drop effect");
        Marshal.WriteInt32(locked, DROPEFFECT_COPY);
        NativeMethods.GlobalUnlock(memory);
        return memory;
    }

    private static nint Allocate(int bytes, string name)
    {
        var memory = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
        if (memory == 0)
        {
            throw new InvalidOperationException(
                $"Clipboard memory for the {name} could not be allocated (error {Marshal.GetLastPInvokeError()}).");
        }

        return memory;
    }

    private static nint Lock(nint memory, string name)
    {
        var locked = NativeMethods.GlobalLock(memory);
        if (locked == 0)
        {
            NativeMethods.GlobalFree(memory);
            throw new InvalidOperationException(
                $"Clipboard memory for the {name} could not be locked (error {Marshal.GetLastPInvokeError()}).");
        }

        return locked;
    }
}
