using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace NopeView.App;

// Image pipeline: open entry points and file status.
public sealed partial class MainWindow : Window
{
    // Animation frames do not need the full open-image UI pass. The frame
    // readout and the automation name still need to catch up eventually, so
    // they are refreshed on this interval instead of on every frame.
    private const int AnimationUiUpdateIntervalMs = 300;
    private long _lastFrameUiUpdate;

    private void OpenImageFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index + 1 < args.Length; index++)
        {
            if (args[index] == "--open")
            {
                _ = OpenImageAsync(args[index + 1]);
                return;
            }
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(OpenImageViaPickerAsync, Strings.OpenTitle);
    }

    private async Task OpenImageViaPickerAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = Localization.Get("Picker.OpenCommit")
        };

        foreach (var extension in SupportedExtensions())
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            picker.FileTypeFilter.Add(normalized);
        }

        var result = await picker.PickSingleFileAsync();
        if (result is null)
        {
            return;
        }
        await OpenImageAsync(result.Path);
    }

    /// <summary>
    /// Extensions the router can decode. The router discovers its formats once
    /// at startup, so the list is built on first use and reused afterwards.
    /// </summary>
    private IReadOnlyList<string> SupportedExtensions()
    {
        if (_supportedExtensions is not null)
        {
            return _supportedExtensions;
        }

        var extensions = new List<string>();
        if (_routerContext != IntPtr.Zero)
        {
            foreach (var format in RouterApi.ListFormats(_routerContext))
            {
                if (!format.CanDecode)
                {
                    continue;
                }
                foreach (var extension in format.Extensions.Split(
                             ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    extensions.Add(extension);
                }
            }
        }

        _supportedExtensions = extensions;
        return extensions;
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        await RunCommandAsync(() => OpenDroppedFileAsync(e), Strings.OpenTitle);
    }

    private async Task OpenDroppedFileAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null)
        {
            await OpenImageAsync(file.Path);
        }
    }

    private async Task OpenImageAsync(string path)
    {
        StopAnimation();
        _frameCount = 1;
        _frameIndex = 0;
        UpdateAnimationControls();
        _directory = ImageDirectory.Create(path, SupportedExtensions());
        UpdateNavigationState();
        await ShowCurrentImageAsync();
    }

    private async Task ShowCurrentImageAsync()
    {
        if (_directory is null)
        {
            return;
        }

        StopAnimation();
        await DecodeAndShowAsync(_directory.CurrentPath);
        UpdateNavigationState();
    }

    private void UpdateFileStatus(string path)
    {
        // The caption carries the product name alone, so the status next to
        // it starts with the document and adds the size and the format.
        var name = Path.GetFileName(path);
        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var size = $"{_imagePixelWidth} x {_imagePixelHeight}";
        FileStatusText.Text = string.IsNullOrEmpty(format)
            ? Localization.Format("Status.File", name, size)
            : Localization.Format("Status.FileWithFormat", name, size, format);

        // The empty state is only for a viewer that has never shown an image.
        EmptyState.Visibility = Visibility.Collapsed;
    }
}
