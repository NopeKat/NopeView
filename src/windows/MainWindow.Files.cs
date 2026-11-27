// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NopeView.App;

// File actions: clipboard, save as, explorer, and status text.
public sealed partial class MainWindow : Window
{
    private void FitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ActualSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyActualSize();
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunCommand(CopyCurrentPath, Strings.CopyTitle);
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        // The clipboard is shared with every other process on the desktop, so
        // another app holding it open is a normal outcome that has to be
        // reported rather than thrown out of the click handler.
        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ShowError(Strings.CopyFailed(ex.Message), Strings.CopyTitle);
        }
    }

    private void CopyFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentFile();
    }

    /// <summary>
    /// Puts the open file on the clipboard as a file, not as pixels, so a
    /// paste into a folder or a mail client copies or attaches the original.
    /// </summary>
    private void CopyCurrentFile()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            ShellClipboard.SetFile(Handle, path);
        }
        catch (Exception ex)
        {
            ShowError(Strings.CopyFailed(ex.Message), Strings.CopyTitle);
        }
    }

    private async void CopyImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(CopyCurrentImageAsync, Strings.CopyTitle);
    }

    private async Task CopyCurrentImageAsync()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        var session = await RouterAsync();
        if (session.Context == IntPtr.Zero)
        {
            ShowError(Strings.RouterUnavailable, Strings.CopyTitle);
            return;
        }

        try
        {
            using var bitmap = await DecodeStraightBitmapAsync(session.Context, path, _frameIndex);
            if (bitmap is null)
            {
                ShowError(Strings.CopyDecodeFailed, Strings.CopyTitle);
                return;
            }

            // The clipboard accepts a bitmap stream, so the decoded frame is
            // encoded to PNG in memory; the clipboard calls below stay on the
            // UI thread, which owns the OLE clipboard.
            var stream = await EncodePngAsync(bitmap);

            var data = new DataPackage();
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(data);
            Clipboard.Flush();

            // The new copy replaced the previous one, so the stream the last
            // copy kept alive is no longer read and goes back here.
            _clipboardImage?.Dispose();
            _clipboardImage = stream;
        }
        catch (Exception ex)
        {
            ShowError(Strings.CopyFailed(ex.Message), Strings.CopyTitle);
        }
    }

    /// <summary>
    /// Decodes the current frame again as straight (non premultiplied) BGRA,
    /// which is what an encoder and the clipboard expect.
    /// </summary>
    private async Task<SoftwareBitmap?> DecodeStraightBitmapAsync(
        IntPtr context, string path, uint frameIndex)
    {
        var maxDimension = FullResolutionEdge(_imagePixelWidth, _imagePixelHeight);

        await _decodeGate.WaitAsync();
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var decoded = RouterImage.Decode(
                    context, path, frameIndex, RouterApi.PixelFormatBgra8,
                    maxDimension, out _);
                if (decoded is null)
                {
                    return null;
                }

                return PixelBuffer.ToSoftwareBitmap(
                    decoded.Value, BitmapAlphaMode.Straight, ExifOrientation.Read(path));
            });

            if (bitmap is null)
            {
                return null;
            }

            NotifyLimitedResolution(maxDimension);
            return bitmap;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>
    /// Encodes a decoded frame to a PNG stream in memory. Encoding keeps the
    /// alpha channel and avoids depending on the source format; the encoder
    /// copies the whole frame, so it runs on a worker thread.
    /// </summary>
    private static async Task<InMemoryRandomAccessStream> EncodePngAsync(SoftwareBitmap bitmap)
    {
        return await Task.Run(async () =>
        {
            var buffer = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            buffer.Seek(0);
            return buffer;
        });
    }

    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(SaveAsAsync, Strings.SaveTitle);
    }

    private async Task SaveAsAsync()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(path),
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = Localization.Get("Picker.SaveCommit")
        };
        picker.FileTypeChoices.Add(
            Localization.Get("Format.Png"), new List<string> { ".png" });
        picker.FileTypeChoices.Add(
            Localization.Get("Format.Jpeg"), new List<string> { ".jpg" });
        picker.FileTypeChoices.Add(
            Localization.Get("Format.Bmp"), new List<string> { ".bmp" });

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var session = await RouterAsync();
            if (session.Context == IntPtr.Zero)
            {
                ShowError(Strings.RouterUnavailable, Strings.SaveTitle);
                return;
            }

            var context = session.Context;
            var frameIndex = _frameIndex;
            var target = result.Path;
            var maxDimension = FullResolutionEdge(_imagePixelWidth, _imagePixelHeight);

            // The frame is decoded again in its original colors. Reusing the
            // displayed bitmap would save premultiplied pixels.
            await _decodeGate.WaitAsync();
            try
            {
                await Task.Run(() => SaveFrame(context, path, frameIndex, target, maxDimension));
            }
            finally
            {
                _decodeGate.Release();
            }

            NotifyLimitedResolution(maxDimension);

            // The folder now holds one more image, so its scan is stale.
            ForgetDirectoryScan(target);
        }
        catch (Exception ex)
        {
            ShowError(Strings.SaveFailed(ex.Message), Strings.SaveTitle);
            return;
        }

        // The file exists now. When it landed next to the image on screen, the
        // list navigation walks has to pick it up, or the new file stays
        // invisible until the folder is opened again. A rescan that fails is
        // not a save failure, so it is kept out of the block above.
        await RefreshDirectoryAfterSaveAsync(path, result.Path);
    }

    /// <summary>
    /// Rebuilds the directory around the open file when a save wrote into the
    /// same folder. A failure here only costs the new file its list entry, and
    /// the next open rebuilds the list anyway.
    /// </summary>
    private async Task RefreshDirectoryAfterSaveAsync(
        string sourcePath, string targetPath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(sourceDirectory) ||
            string.IsNullOrEmpty(targetDirectory) ||
            !string.Equals(
                sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _directory = await CreateDirectoryAsync(sourcePath);
            UpdateNavigationState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save: could not refresh the folder list: {ex.Message}");
        }
    }

    private static void SaveFrame(
        IntPtr context,
        string sourcePath,
        uint frameIndex,
        string targetPath,
        int maxDimension)
    {
        using var decoded = RouterImage.Decode(
            context, sourcePath, frameIndex, RouterApi.PixelFormatRgba8,
            maxDimension, out var status);
        if (decoded is null)
        {
            throw new InvalidOperationException(Strings.DescribeDecodeStatus(status));
        }

        // The encoder writes the pixels it is given, so a camera photo has to
        // be turned here as well; otherwise the file lands sideways while the
        // screen shows it upright.
        var orientation = ExifOrientation.Read(sourcePath);
        if (orientation == ExifOrientation.Normal)
        {
            var plain = decoded.Value;
            var plainStatus = RouterApi.EncodeToFile(context, targetPath, ref plain);
            if (plainStatus != 0)
            {
                throw new InvalidOperationException(Strings.DescribeEncodeStatus(plainStatus));
            }
            return;
        }

        var turned = PixelBuffer.Turn(decoded.Value, orientation);
        try
        {
            var encodeStatus = RouterApi.EncodeToFile(context, targetPath, ref turned);
            if (encodeStatus != 0)
            {
                throw new InvalidOperationException(Strings.DescribeEncodeStatus(encodeStatus));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(turned.Pixels);
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunCommand(ShowCurrentInExplorer, Strings.ShowInExplorerTitle);
    }

    /// <summary>
    /// Opens the shell view around the current file. The shell refuses when the
    /// file or its folder is gone, and that is reported instead of leaving the
    /// command silently dead.
    /// </summary>
    private void ShowCurrentInExplorer()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError(Strings.ShowInExplorerFailed(ex.Message), Strings.ShowInExplorerTitle);
        }
    }

    private async void CopyImageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(CopyCurrentImageAsync, Strings.CopyTitle);
    }

    private void CopyPathAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RunCommand(CopyCurrentPath, Strings.CopyTitle);
    }

    private async void SaveAsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(SaveAsAsync, Strings.SaveTitle);
    }
}
