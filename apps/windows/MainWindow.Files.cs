using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Diagnostics;
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
        CopyCurrentPath();
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        var data = new DataPackage();
        data.SetText(path);
        Clipboard.SetContent(data);
        Clipboard.Flush();
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

        try
        {
            using var bitmap = await DecodeStraightBitmapAsync(path, _frameIndex);
            if (bitmap is null)
            {
                ShowError(Strings.CopyDecodeFailed, Strings.CopyTitle);
                return;
            }

            // The clipboard accepts a bitmap stream, so the decoded frame is
            // encoded to PNG in memory. Encoding keeps the alpha channel and
            // avoids depending on the source format.
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            stream.Seek(0);

            var data = new DataPackage();
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(data);
            Clipboard.Flush();

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
    private async Task<SoftwareBitmap?> DecodeStraightBitmapAsync(string path, uint frameIndex)
    {
        var context = _routerContext;
        await _decodeGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var decoded = RouterImage.Decode(
                    context, path, frameIndex, RouterApi.PixelFormatBgra8, 0, out _);
                if (decoded is null)
                {
                    return null;
                }

                return PixelBuffer.ToSoftwareBitmap(decoded.Value, BitmapAlphaMode.Straight);
            });
        }
        finally
        {
            _decodeGate.Release();
        }
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
            CommitButtonText = "Save"
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("BMP image", new List<string> { ".bmp" });

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var context = _routerContext;
            var frameIndex = _frameIndex;
            var target = result.Path;

            // The frame is decoded again in its original colors. Reusing the
            // displayed bitmap would save premultiplied pixels.
            await _decodeGate.WaitAsync();
            try
            {
                await Task.Run(() => SaveFrame(context, path, frameIndex, target));
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (Exception ex)
        {
            ShowError(Strings.SaveFailed(ex.Message), Strings.SaveTitle);
        }
    }

    private static void SaveFrame(
        IntPtr context,
        string sourcePath,
        uint frameIndex,
        string targetPath)
    {
        using var decoded = RouterImage.Decode(
            context, sourcePath, frameIndex, RouterApi.PixelFormatRgba8, 0, out var status);
        if (decoded is null)
        {
            throw new InvalidOperationException(Strings.DescribeDecodeStatus(status));
        }

        var image = decoded.Value;
        var encodeStatus = RouterApi.EncodeToFile(context, targetPath, ref image);
        if (encodeStatus != 0)
        {
            throw new InvalidOperationException(Strings.DescribeEncodeStatus(encodeStatus));
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        });
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
        CopyCurrentPath();
    }

    private async void SaveAsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(SaveAsAsync, Strings.SaveTitle);
    }
}
