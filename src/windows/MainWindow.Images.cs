using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using System.Diagnostics;
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

    // One scan per folder per session. Enumerating a folder is disk work, so it
    // runs on a worker thread and the result is reused while the user moves
    // through the images of the same folder.
    private readonly Dictionary<string, Task<IReadOnlyList<string>>> _directoryScans =
        new(StringComparer.OrdinalIgnoreCase);

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
        // The filter list is built from the formats the router discovered, so
        // the picker waits for the setup instead of enumerating formats here.
        await RouterAsync();

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
    /// Extensions the router can decode, filled in by the router setup. Until
    /// that setup finishes the list is empty, which only affects the picker
    /// filters of a command that is already waiting for it.
    /// </summary>
    private IReadOnlyList<string> SupportedExtensions()
    {
        return _supportedExtensions ?? Array.Empty<string>();
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
        _upgradeTimer.Stop();
        _frameCount = 1;
        _frameIndex = 0;
        UpdateAnimationControls();

        var wait = Stopwatch.StartNew();
        var session = await RouterAsync();
        wait.Stop();
        UiPerf.Record("router-wait", wait.Elapsed, "open");

        if (session.Context == IntPtr.Zero)
        {
            ShowError(Strings.RouterUnavailable);
            return;
        }

        _directory = await CreateDirectoryAsync(path);
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

    /// <summary>
    /// Builds the directory around the opened file. The scan runs off the UI
    /// thread, so a large or slow folder delays the load instead of the input
    /// that asked for it.
    /// </summary>
    private async Task<ImageDirectory> CreateDirectoryAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        IReadOnlyList<string> paths = Array.Empty<string>();

        if (!string.IsNullOrEmpty(directory))
        {
            var scan = Stopwatch.StartNew();
            paths = await ScanDirectoryAsync(directory);
            scan.Stop();
            UiPerf.Record("dir-scan", scan.Elapsed, $"{paths.Count} paths");
        }

        return ImageDirectory.FromPaths(fullPath, paths);
    }

    private Task<IReadOnlyList<string>> ScanDirectoryAsync(string directory)
    {
        if (_directoryScans.TryGetValue(directory, out var cached))
        {
            return cached;
        }

        var extensions = SupportedExtensions().ToArray();
        var scan = Task.Run<IReadOnlyList<string>>(() =>
            FilesApi.TryListPaths(directory, extensions, out var paths)
                ? paths
                : Array.Empty<string>());

        _directoryScans[directory] = scan;
        return scan;
    }

    /// <summary>
    /// Drops a cached scan after the app wrote a file into that folder, so the
    /// next open sees the new file instead of the list from before the write.
    /// </summary>
    private void ForgetDirectoryScan(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _directoryScans.Remove(directory);
        }
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
