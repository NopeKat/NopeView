using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NopeView.App;

public sealed partial class MainWindow : Window
{
    // Restore size used when the window is un-maximized. The window opens
    // maximized, so this is what restore actions return to.
    private const int PreferredWidth = 1280;
    private const int PreferredHeight = 840;

    // The restore size is also capped to this fraction of the work area so that
    // un-maximizing always produces a visibly smaller window, even on a display
    // where the preferred size would cover the whole screen.
    private const double RestoreAreaRatio = 0.9;

    // App.xaml's CaptionGlyphBrush is the only source for the active color.
    // Only the inactive greys are created here, and they are cached.
    private static readonly SolidColorBrush s_darkInactiveBrush =
        new(ColorHelper.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush s_lightInactiveBrush =
        new(ColorHelper.FromArgb(255, 153, 153, 153));

    private readonly SoftwareBitmapSource _imageSource = new();

    private bool _titleBarInitialized;
    private bool _windowActive = true;
    private nint _windowHandle;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;

    private IntPtr _routerContext;
    private ImageDirectory? _directory;
    private double _zoom = 1.0;
    private double _rotation;
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    public MainWindow()
    {
        InitializeComponent();
        _windowHandle = Handle;
        ImageView.Source = _imageSource;

        if (!RouterApi.TryCreate(null, out _routerContext))
        {
            _routerContext = IntPtr.Zero;
        }

        // Set the window icon explicitly. For an unpackaged WinUI 3 app the
        // executable's ApplicationIcon alone does not reach Explorer, so the
        // taskbar button, Alt-Tab and thumbnails show a default icon until the
        // running window is given one through AppWindow.SetIcon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // Extend content into the title bar so the custom XAML row can own the
        // drag region. The native strip is collapsed later in RootGrid_Loaded.
        ExtendsContentIntoTitleBar = true;

        ConfigureStartupWindow();

        // The caption glyphs turn grey when the window loses focus, exactly as
        // the native title bar buttons do. Both activation and theme changes
        // re-evaluate the glyph color.
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionAppearance();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateCaptionAppearance();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Win32Window.UnsuppressSystemMenu(_windowHandle);
        RouterApi.Close(_routerContext);
        _routerContext = IntPtr.Zero;
    }

    /// <summary>
    /// Applies the active/inactive glyph color to the self-drawn caption
    /// buttons and the title text. Native values: white/black while active,
    /// #808080 (dark) or #999999 (light) while inactive. Hover and pressed
    /// states override this through the button templates.
    /// </summary>
    private void UpdateCaptionAppearance()
    {
        if (!_titleBarInitialized)
        {
            return;
        }

        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var activeBrush = (SolidColorBrush)Application.Current.Resources["CaptionGlyphBrush"];
        var brush = _windowActive
            ? activeBrush
            : (dark ? s_darkInactiveBrush : s_lightInactiveBrush);

        MinimizeButton.Foreground = brush;
        RestoreButton.Foreground = brush;
        CloseButton.Foreground = brush;
        TitleText.Foreground = brush;
    }

    private nint Handle => WindowNative.GetWindowHandle(this);

    /// <summary>
    /// Rasterization scale of the window. XamlRoot is authoritative once the
    /// content is loaded; before that, the monitor DPI is the only source.
    /// </summary>
    private double Scale
    {
        get
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 0;
            return scale > 0 ? scale : Win32Window.GetScaleFactor(Handle);
        }
    }

    /// <summary>
    /// Centers the window at the preferred size, then maximizes it so the
    /// content gets the whole work area. The centered size stays as the restore
    /// size. Running this before the window is shown means it opens maximized
    /// with no visible resize flash.
    /// </summary>
    private void ConfigureStartupWindow()
    {
        var appWindow = AppWindow;
        var scale = Scale;
        var workArea = DisplayArea
            .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        // Keep the restore size comfortably inside the work area: on a high-DPI
        // display the preferred size can match the full screen, which would make
        // un-maximizing look like nothing changed.
        var widthDip = Math.Min(PreferredWidth, workArea.Width / scale * RestoreAreaRatio);
        var heightDip = Math.Min(PreferredHeight, workArea.Height / scale * RestoreAreaRatio);
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        appWindow.MoveAndResize(new RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_titleBarInitialized)
        {
            return;
        }
        _titleBarInitialized = true;

        // Only the options that differ from the defaults are set here; the
        // extended title bar itself was enabled in the constructor.
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            // Collapse the system title bar entirely. This removes the native
            // caption buttons and their tooltips; the XAML buttons below replace
            // them and reproduce their geometry and colors.
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }

        // Register the custom row as the drag region now that the system strip
        // is gone. AppTitleBar owns the whole caption area.
        SetTitleBar(AppTitleBar);

        // Keep the maximize/restore glyph in sync with the window state, both
        // on button clicks and on state changes from elsewhere (double click,
        // snap layouts, keyboard).
        UpdateCaptionIcons();
        AppWindow.Changed += AppWindow_Changed;

        // The native system menu is unwanted on the custom title bar. Filter the
        // messages that open it instead of clearing WS_SYSMENU, which would also
        // shift the caption buttons off the visible right edge.
        if (!Win32Window.TrySuppressSystemMenu(_windowHandle))
        {
            System.Diagnostics.Debug.WriteLine("Failed to install the system menu filter.");
        }

        UpdateCaptionAppearance();
        OpenImageFromCommandLine();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Presenter state (maximized/restored/minimized) changes are reported
        // here, including ones not caused by our own caption buttons. The check
        // is cheap, so refresh on every change rather than inspecting args.
        UpdateCaptionIcons();
    }

    /// <summary>
    /// Shows the restore glyph while maximized and the maximize glyph while
    /// restored, matching the native caption button behavior.
    /// </summary>
    private void UpdateCaptionIcons()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;

        // A maximized window sits at an odd screen offset, which shifts the
        // half-pixel glyph nudge used to align with the native glyphs. Keep the
        // nudge only in the restored state so both states stay pixel-aligned.
        // The minimize glyph sits one pixel lower than the other two, so it
        // needs an extra half-pixel nudge.
        var offset = maximized ? 0.0 : -0.5;
        MinimizeOffset.Y = offset - 0.5;
        CloseOffset.Y = offset;
    }

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
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = "Open image"
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

    private IReadOnlyList<RouterFormatInfo> SupportedFormats()
    {
        return _routerContext == IntPtr.Zero
            ? Array.Empty<RouterFormatInfo>()
            : RouterApi.ListFormats(_routerContext);
    }

    private IEnumerable<string> SupportedExtensions()
    {
        foreach (var format in SupportedFormats())
        {
            if (!format.CanDecode)
            {
                continue;
            }
            foreach (var extension in format.Extensions.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return extension;
            }
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
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

        await DecodeAndShowAsync(_directory.CurrentPath);
        UpdateNavigationState();
    }

    private async Task DecodeAndShowAsync(string path)
    {
        if (_routerContext == IntPtr.Zero)
        {
            ShowError("Codec router is unavailable.");
            return;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;

        try
        {
            if (!RouterApi.TryDecodeFile(
                    _routerContext, path, out var image, out var status,
                    0u, RouterApi.PixelFormatBgra8))
            {
                ShowError(DescribeStatus(status));
                return;
            }

            SoftwareBitmap bitmap;
            try
            {
                bitmap = ToSoftwareBitmap(ref image);
            }
            finally
            {
                RouterApi.FreeImage(ref image);
            }

            await _imageSource.SetBitmapAsync(bitmap);
            _imagePixelWidth = bitmap.PixelWidth;
            _imagePixelHeight = bitmap.PixelHeight;
            ResetView();
            SetViewControlsEnabled(true);
            ViewStatusPanel.Visibility = Visibility.Visible;
            AutomationProperties.SetName(ImageView, "Current image: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowError("Failed to open image: " + ex.Message);
        }
    }

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = _directory?.HasPrevious == true;
        NextButton.IsEnabled = _directory?.HasNext == true;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await MovePreviousAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await MoveNextAsync();
    }

    private async void PreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MovePreviousAsync();
    }

    private async void NextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MoveNextAsync();
    }

    private async Task MovePreviousAsync()
    {
        if (_directory is not null && _directory.MovePrevious())
        {
            await ShowCurrentImageAsync();
        }
    }

    private async Task MoveNextAsync()
    {
        if (_directory is not null && _directory.MoveNext())
        {
            await ShowCurrentImageAsync();
        }
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
        };
    }

    private void PanThumb_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(PanThumb).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        SetZoom(delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
        e.Handled = true;
    }

    private void PanThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        ImageTransform.TranslateX += e.HorizontalChange;
        ImageTransform.TranslateY += e.VerticalChange;
        UpdateViewStatus();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom / ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom * ZoomStep);
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        ImageTransform.Rotation = _rotation;
        UpdateViewStatus();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        var ratio = FitToPixelRatio;
        if (ratio <= 0)
        {
            return;
        }

        SetZoom(1.0 / ratio);
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
    }

    private double FitToPixelRatio
    {
        get
        {
            if (_imagePixelWidth <= 0 || ImageView.ActualWidth <= 0)
            {
                return 1.0;
            }

            var rasterizationScale = Viewport.XamlRoot?.RasterizationScale ?? 1.0;
            return ImageView.ActualWidth * rasterizationScale / _imagePixelWidth;
        }
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        UpdateViewStatus();
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _rotation = 0;
        ImageTransform.ScaleX = 1.0;
        ImageTransform.ScaleY = 1.0;
        ImageTransform.Rotation = 0;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        var rotation = Math.Round(_rotation);
        var panX = Math.Round(ImageTransform.TranslateX);
        var panY = Math.Round(ImageTransform.TranslateY);
        var pan = Math.Abs(panX) >= 1 || Math.Abs(panY) >= 1
            ? $" | pan {panX:0},{panY:0}"
            : "";

        if (Math.Abs(_zoom - 1.0) < 0.001)
        {
            ViewStatusText.Text = $"Fit | {rotation:0} deg{pan}";
            return;
        }

        var percent = _zoom * FitToPixelRatio * 100.0;
        ViewStatusText.Text = $"{Math.Round(percent):0}% | {rotation:0} deg{pan}";
    }

    private void SetViewControlsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RotateButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
    }

    private static SoftwareBitmap ToSoftwareBitmap(ref NvRouterImage image)
    {
        var pixelCount = image.Width * image.Height * 4;
        var pixels = new byte[pixelCount];
        Marshal.Copy(image.Pixels, pixels, 0, pixelCount);

        var swap = image.PixelFormat != RouterApi.PixelFormatBgra8;
        for (var index = 0; index < pixelCount; index += 4)
        {
            var first = pixels[index];
            var third = pixels[index + 2];
            var alpha = pixels[index + 3];

            if (swap)
            {
                pixels[index] = third;
                pixels[index + 2] = first;
            }

            // SoftwareBitmap requires premultiplied alpha.
            pixels[index] = (byte)(pixels[index] * alpha / 255);
            pixels[index + 1] = (byte)(pixels[index + 1] * alpha / 255);
            pixels[index + 2] = (byte)(pixels[index + 2] * alpha / 255);
        }

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        var buffer = writer.DetachBuffer();

        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string DescribeStatus(int status)
    {
        return status switch
        {
            RouterApi.ErrUnsupportedFormat => "Unsupported image format.",
            RouterApi.ErrDecodeFailed => "The image is damaged or cannot be decoded.",
            RouterApi.ErrOutOfMemory => "Not enough memory to decode this image.",
            RouterApi.ErrIo => "The file could not be read.",
            RouterApi.ErrAbiMismatch => "Codec ABI mismatch.",
            _ => $"Failed to open image (status {status})."
        };
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
            UpdateCaptionIcons();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
