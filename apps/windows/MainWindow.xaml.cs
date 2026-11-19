using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    // Caption glyph colors live in App.xaml's theme dictionaries. The active
    // color comes from each element's own theme default, and the inactive
    // greys are looked up per element theme, so the title bar follows the app
    // theme instead of the application-level (system) theme.

    private readonly DispatcherTimer _animationTimer = new();

    private bool _titleBarInitialized;
    private bool _windowActive = true;
    private nint _windowHandle;
    // Pressed visual state of a caption button held down while the window
    // move loop is active; reported by the native caption press filter.
    private Button? _movePressedButton;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;

    // Large images are decoded once at a reduced size for display. The preview
    // long edge follows the viewport in physical pixels, clamped to these
    // bounds. A full decode replaces it when the view magnifies the pixels.
    private const int PreviewMinEdge = 1024;
    private const int PreviewMaxEdge = 4096;

    private IntPtr _routerContext;
    private IReadOnlyList<string>? _supportedExtensions;
    private ImageDirectory? _directory;
    private double _zoom = 1.0;
    private double _rotation;
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    // Touch contacts currently on the viewport. Two or more turn the gesture
    // into a pinch, which scales and pans around the contact midpoint while
    // the thumb drag is ignored.
    private readonly Dictionary<uint, Point> _touchPoints = new();
    private Point _pinchCenter;
    private double _pinchDistance;
    private bool _isPinching;

    // True while the displayed bitmap is a downscaled preview. The status bar
    // still reports the source dimensions, and zooming past the preview scale
    // triggers a full decode.
    private bool _previewActive;

    private uint _frameIndex;
    private uint _frameCount = 1;
    private uint _frameDelayMs = 100;
    private bool _isAnimationPlaying;
    private bool _animationLoop = true;
    private bool _animationBusy;

    // Animation timing runs on absolute deadlines. A relative interval per
    // tick accumulates the timer's own latency into every frame, which makes a
    // long animation visibly slower than its declared delays.
    private readonly Stopwatch _animationClock = new();
    private TimeSpan _animationDeadline;

    private bool _isFullScreen;
    private bool _wasMaximizedBeforeFullScreen;

    // The clipboard reads the stream after SetContent returns, so it must
    // outlive the call.
    private InMemoryRandomAccessStream? _clipboardImage;

    // Frames are decoded on a worker thread. A newer request makes an older
    // one stale, so the generation counter discards the superseded result
    // instead of letting it overwrite the newer image.
    private int _decodeGeneration;

    // A single source is reused for every frame. Creating a source per frame
    // allocates a new native image object for each animation step.
    private readonly SoftwareBitmapSource _frameSource = new();

    private readonly Storyboard _loadingAnimation;

    // User preferences are loaded at startup and saved as soon as a choice is
    // made on the settings page. Theme is applied to the root grid so the local
    // ThemeDictionaries in App.xaml switch without touching system brushes.
    private readonly SettingsService _settings = new();

    // Settings lives in its own window so the viewer stays usable while the
    // page is open. A null field means no settings window is on screen.
    private SettingsWindow? _settingsWindow;

    // The settings window opens at 80% of the viewer's current size.
    private const double SettingsSizeRatio = 0.8;

    public MainWindow()
    {
        InitializeComponent();
        // Mica is the Fluent window backdrop. It only becomes visible while
        // the settings page is shown; the viewer keeps its own solid surface.
        SystemBackdrop = new MicaBackdrop();
        _loadingAnimation = CreateLoadingAnimation();

        // The thumb marks pointer events as handled while it drags, so the
        // pinch tracking below listens to handled events as well.
        PanThumb.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(PanThumb_TouchPressed),
            true);
        PanThumb.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(PanThumb_TouchMoved),
            true);
        PanThumb.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(PanThumb_TouchReleased),
            true);
        PanThumb.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(PanThumb_TouchReleased),
            true);
        PanThumb.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(PanThumb_TouchReleased),
            true);
        _windowHandle = Handle;
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(100);
        AddOemKeyAccelerators();

        if (!RouterApi.TryCreate(null, out _routerContext))
        {
            _routerContext = IntPtr.Zero;
        }

        _settings.Load();
        ApplyTheme(_settings.Theme);

        // Set the window icon explicitly. For an unpackaged WinUI 3 app the
        // executable's ApplicationIcon alone does not reach Explorer, so the
        // taskbar button, Alt-Tab and thumbnails show a default icon until the
        // running window is given one through AppWindow.SetIcon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "nopeview.ico");
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

    /// <summary>
    /// Adds the accelerators for keys that Windows.System.VirtualKey does not
    /// name. The main keyboard's '+' and '-' are OEM keys, so they exist only
    /// as raw virtual-key codes and cannot be declared in XAML.
    /// </summary>
    private void AddOemKeyAccelerators()
    {
        AddAccelerator(0xBB, Windows.System.VirtualKeyModifiers.None, ZoomInAccelerator_Invoked);
        AddAccelerator(0xBB, Windows.System.VirtualKeyModifiers.Shift, ZoomInAccelerator_Invoked);
        AddAccelerator(0xBD, Windows.System.VirtualKeyModifiers.None, ZoomOutAccelerator_Invoked);
        AddAccelerator(0xBC, Windows.System.VirtualKeyModifiers.Control, SettingsAccelerator_Invoked);
    }

    private void AddAccelerator(
        int key,
        Windows.System.VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)key,
            Modifiers = modifiers
        };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateCaptionAppearance();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // The settings window is independent, so closing the viewer must also
        // close it or the process would keep running without a main window.
        _settingsWindow?.Close();
        _settingsWindow = null;

        _animationTimer.Stop();
        Win32Window.UnsuppressSystemMenu(_windowHandle);
        Win32Window.UninstallCaptionPressFilter(_windowHandle);
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

        var theme = RootGrid.ActualTheme;
        if (_windowActive)
        {
            // Clearing the local value restores the style setter, so the glyph
            // resolves against this element's theme. Reading a brush from
            // Application.Current.Resources would resolve against the system
            // theme instead, which painted white glyphs on a light title bar.
            MinimizeButton.ClearValue(Control.ForegroundProperty);
            RestoreButton.ClearValue(Control.ForegroundProperty);
            CloseButton.ClearValue(Control.ForegroundProperty);
            TitleText.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        // High contrast has no key of its own yet, so a missing brush leaves
        // the elements on their theme default rather than throwing.
        var inactiveBrush = GetThemeBrush("CaptionGlyphInactiveBrush", theme);
        if (inactiveBrush is null)
        {
            return;
        }

        MinimizeButton.Foreground = inactiveBrush;
        RestoreButton.Foreground = inactiveBrush;
        CloseButton.Foreground = inactiveBrush;
        TitleText.Foreground = inactiveBrush;
    }

    /// <summary>
    /// Looks a brush up in App.xaml's theme dictionary for a specific element
    /// theme, bypassing the application-level theme that a direct
    /// Application.Current.Resources lookup would use.
    /// </summary>
    private static Brush? GetThemeBrush(string key, ElementTheme theme)
    {
        var dictionaryKey = theme == ElementTheme.Dark ? "Dark" : "Light";
        var dictionaries = Application.Current.Resources.ThemeDictionaries;
        if (!dictionaries.TryGetValue(dictionaryKey, out var entry) ||
            entry is not ResourceDictionary dictionary ||
            !dictionary.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as Brush;
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

        // Presses on the caption buttons start the window move loop before
        // the XAML buttons see the pointer, so their pressed visual state
        // never engages on its own (hover and click still work). The native
        // subclass below reports the non-client presses so the pressed state
        // can be shown manually. The WinRT InputNonClientPointerSource is not
        // usable for this on the current SDK: obtaining it switches the title
        // bar input routing away from the SetTitleBar regions and stops the
        // button clicks from working entirely.
        if (!Win32Window.TryInstallCaptionPressFilter(
                _windowHandle,
                OnCaptionPressFeedback,
                OnCaptionReleaseFeedback))
        {
            System.Diagnostics.Debug.WriteLine("Failed to install the caption press filter.");
        }

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

    /// <summary>
    /// Shows the pressed visual state on the caption button the user is
    /// holding, called from the native filter when a non-client press lands
    /// in the button. The state is reverted when the press ends. The work is
    /// queued instead of running inline: the filter callback happens inside
    /// the window message procedure, and re-entering XAML synchronously
    /// there breaks the move loop that follows the press.
    /// </summary>
    private void OnCaptionPressFeedback(int screenX, int screenY)
    {
        RootGrid.DispatcherQueue.TryEnqueue(() => ApplyCaptionPressedState(screenX, screenY));
    }

    private void OnCaptionReleaseFeedback()
    {
        RootGrid.DispatcherQueue.TryEnqueue(ClearCaptionPressedState);
    }

    private void ApplyCaptionPressedState(int screenX, int screenY)
    {
        var scale = Win32Window.GetScaleFactor(_windowHandle);
        if (scale <= 0)
        {
            return;
        }

        // The screen coordinates and the window origin are physical pixels.
        var x = (screenX - AppWindow.Position.X) / scale;
        var y = (screenY - AppWindow.Position.Y) / scale;
        var button = FindCaptionButtonAt(x, y);
        if (button is null)
        {
            return;
        }

        _movePressedButton = button;
        VisualStateManager.GoToState(button, "Pressed", false);
    }

    private void ClearCaptionPressedState()
    {
        if (_movePressedButton is null)
        {
            return;
        }

        VisualStateManager.GoToState(_movePressedButton, "Normal", false);
        _movePressedButton = null;
    }

    /// <summary>
    /// Returns the visible caption button whose bounds contain the given
    /// window coordinate in XAML units, or null when the point is outside
    /// every button (a plain window move or resize press).
    /// </summary>
    private Button? FindCaptionButtonAt(double x, double y)
    {
        foreach (var button in new[] { MinimizeButton, RestoreButton, CloseButton })
        {
            if (button.Visibility != Visibility.Visible ||
                button.ActualWidth <= 0 ||
                button.ActualHeight <= 0)
            {
                continue;
            }

            var bounds = button.TransformToVisual(null).TransformBounds(
                new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            if (x >= bounds.X && x <= bounds.X + bounds.Width &&
                y >= bounds.Y && y <= bounds.Y + bounds.Height)
            {
                return button;
            }
        }

        return null;
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
        await RunCommandAsync(OpenImageViaPickerAsync, "Could not open image");
    }

    private async Task OpenImageViaPickerAsync()
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
        await RunCommandAsync(() => OpenDroppedFileAsync(e), "Could not open image");
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

    private async Task DecodeAndShowAsync(
        string path,
        uint frameIndex = 0u,
        bool resetView = true,
        bool fullResolution = false)
    {
        if (_routerContext == IntPtr.Zero)
        {
            ShowError("Codec router is unavailable.");
            return;
        }

        var generation = ++_decodeGeneration;
        if (resetView)
        {
            // Only a full open shows the indicator. Animation frames are short
            // enough that a ring would flicker on every step.
            ErrorPanel.Visibility = Visibility.Collapsed;
            SetLoading(true);
        }

        try
        {
            // Decoding, the pixel copy, and the premultiply step all run on a
            // worker thread. Leaving them on the UI thread froze the window for
            // the whole decode and prevented a loading state from rendering.
            var context = _routerContext;
            var maxDimension = fullResolution ? 0 : PreviewEdge();
            var frame = await Task.Run(() => DecodeFrame(context, path, frameIndex, maxDimension));

            if (generation != _decodeGeneration)
            {
                // A newer request already owns the view.
                frame.Bitmap?.Dispose();
                return;
            }

            if (frame.Error is not null)
            {
                FailDecode("Failed to open image: " + frame.Error);
                return;
            }

            if (frame.Bitmap is null)
            {
                FailDecode(DescribeStatus(frame.Status));
                return;
            }

            try
            {
                await _frameSource.SetBitmapAsync(frame.Bitmap);
            }
            finally
            {
                // The source copies the pixels, so the decoded bitmap is no
                // longer needed once the update completes.
                frame.Bitmap.Dispose();
            }

            if (generation != _decodeGeneration)
            {
                return;
            }

            if (!ReferenceEquals(ImageView.Source, _frameSource))
            {
                ImageView.Source = _frameSource;
            }

            _imagePixelWidth = frame.SourceWidth;
            _imagePixelHeight = frame.SourceHeight;
            _previewActive = frame.SourceWidth > frame.BitmapWidth;
            _frameIndex = frameIndex;
            _frameCount = Math.Max(1u, frame.FrameCount);
            _frameDelayMs = frame.FrameDelayMs;
            SetViewControlsEnabled(true);
            UpdateFileStatus(path);
            UpdateViewStatus();
            UpdateAnimationControls();
            UpdateImageAutomationName(path);

            if (resetView)
            {
                ResetView();
                if (_frameCount > 1)
                {
                    _isAnimationPlaying = true;
                    UpdateAnimationControls();
                    ScheduleNextFrame();
                }
                else
                {
                    StopAnimation();
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("Failed to open image: " + ex.Message);
            if (!resetView)
            {
                StopAnimation();
            }
        }
        finally
        {
            // A superseded open must not hide the indicator, because the newer
            // request is still decoding behind it.
            if (resetView && generation == _decodeGeneration)
            {
                SetLoading(false);
            }
        }

        // Both failure paths share the same state repair: the preview bitmap is
        // still on screen after a failed full decode, and animation stops when a
        // frame fails instead of spinning on a broken source.
        void FailDecode(string message)
        {
            ShowError(message);
            if (fullResolution)
            {
                _previewActive = true;
            }
            if (!resetView)
            {
                StopAnimation();
            }
        }
    }

    /// <summary>
    /// Decodes one frame entirely on the calling thread. The router image is
    /// released here, so no native pointer ever crosses threads.
    /// </summary>
    private DecodedFrame DecodeFrame(IntPtr context, string path, uint frameIndex, int maxDimension)
    {
        var frame = new DecodedFrame();

        try
        {
            using var decoded = RouterImage.Decode(
                context, path, frameIndex, RouterApi.PixelFormatBgra8, maxDimension, out var status);
            if (decoded is null)
            {
                frame.Status = status;
                return frame;
            }

            var image = decoded.Value;
            frame.Bitmap = PixelBuffer.ToSoftwareBitmap(image, BitmapAlphaMode.Premultiplied);
            frame.BitmapWidth = image.Width;
            frame.SourceWidth = image.SourceWidth > 0 ? image.SourceWidth : image.Width;
            frame.SourceHeight = image.SourceHeight > 0 ? image.SourceHeight : image.Height;
            frame.FrameCount = image.FrameCount;
            frame.FrameDelayMs = image.FrameDelayMs;
        }
        catch (Exception ex)
        {
            frame.Error = ex.Message;
        }

        return frame;
    }

    private sealed class DecodedFrame
    {
        internal SoftwareBitmap? Bitmap;
        internal int Status;
        internal int BitmapWidth;
        internal int SourceWidth;
        internal int SourceHeight;
        internal uint FrameCount;
        internal uint FrameDelayMs;
        internal string? Error;
    }

    /// <summary>
    /// Longest viewport edge in physical pixels, clamped so the preview stays
    /// sharp at fit on small displays and bounded on very large ones.
    /// </summary>
    private int PreviewEdge()
    {
        var scale = Viewport.XamlRoot?.RasterizationScale ?? Scale;
        var longest = Math.Max(Viewport.ActualWidth, Viewport.ActualHeight) * scale;
        return Math.Clamp((int)Math.Round(longest), PreviewMinEdge, PreviewMaxEdge);
    }

    /// <summary>
    /// Replaces the preview bitmap with a full-size decode once the view
    /// magnifies its pixels. One attempt per load; animated frames keep the
    /// preview so playback does not decode full frames.
    /// </summary>
    private async Task UpgradeToFullResolutionAsync()
    {
        if (!_previewActive || _frameCount > 1 || _directory is null)
        {
            return;
        }

        // Cleared before the decode so overlapping zoom input cannot queue a
        // second full decode; the decode result sets it again.
        _previewActive = false;
        await DecodeAndShowAsync(
            _directory.CurrentPath, _frameIndex, resetView: false, fullResolution: true);
    }

    private void SetLoading(bool loading)
    {
        var visible = LoadingRing.Visibility == Visibility.Visible;
        if (loading == visible)
        {
            return;
        }

        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        // The rotation is a dependent animation, so it only needs to run while
        // the indicator is on screen.
        if (loading)
        {
            _loadingAnimation.Begin();
        }
        else
        {
            _loadingAnimation.Stop();
        }
    }

    /// <summary>
    /// Spins the dashed loading ring. A classic storyboard is used because a
    /// ProgressRing's Lottie visual crashes the current WinUI build.
    /// </summary>
    private Storyboard CreateLoadingAnimation()
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1.2)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, LoadingSpinnerRotation);
        Storyboard.SetTargetProperty(spin, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(spin);
        return storyboard;
    }

    private void UpdateImageAutomationName(string path)
    {
        var name = "Current image: " + Path.GetFileName(path);
        if (_previewActive)
        {
            // Tells assistive tech that the displayed bitmap is downscaled.
            name += " (preview)";
        }
        if (_frameCount > 1)
        {
            name += $" [frame {_frameIndex + 1}/{_frameCount}]";
        }
        AutomationProperties.SetName(ImageView, name);
    }

    private void AnimationTimer_Tick(object? sender, object e)
    {
        _ = RunCommandAsync(AdvanceAnimationAsync, "Could not play animation");
    }

    private async Task AdvanceAnimationAsync()
    {
        _animationTimer.Stop();
        if (!_isAnimationPlaying || _animationBusy || _directory is null)
        {
            return;
        }

        if (_frameCount <= 1)
        {
            StopAnimation();
            return;
        }

        var next = _frameIndex + 1;
        if (next >= _frameCount)
        {
            if (!_animationLoop)
            {
                StopAnimation();
                return;
            }
            next = 0;
        }

        var path = _directory.CurrentPath;
        _animationBusy = true;
        try
        {
            await DecodeAndShowAsync(path, next, resetView: false);
        }
        finally
        {
            _animationBusy = false;
        }

        if (_isAnimationPlaying)
        {
            ScheduleNextFrame();
        }
    }

    private void ScheduleNextFrame()
    {
        if (!_isAnimationPlaying || _frameCount <= 1)
        {
            return;
        }

        if (!_animationClock.IsRunning)
        {
            _animationClock.Restart();
            _animationDeadline = TimeSpan.Zero;
        }

        var delay = TimeSpan.FromMilliseconds(
            Math.Clamp((double)_frameDelayMs, 20.0, 10000.0));
        var now = _animationClock.Elapsed;

        // Add the delay to the previous deadline rather than to the moment the
        // decode finished, so timer latency stays out of the frame duration.
        _animationDeadline += delay;
        if (_animationDeadline < now)
        {
            // A slow decode put us behind schedule. Resume from now instead of
            // firing a burst of catch-up frames.
            _animationDeadline = now;
        }

        _animationTimer.Interval = _animationDeadline - now;
        _animationTimer.Start();
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        _isAnimationPlaying = false;
        _animationBusy = false;
        UpdateAnimationControls();
    }

    private void UpdateAnimationControls()
    {
        var animated = _frameCount > 1;
        AnimationControls.Visibility = animated ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.IsEnabled = animated;
        LoopButton.IsEnabled = animated;
        PlayPauseLabel.Text = _isAnimationPlaying ? "Pause" : "Play";
        LoopLabel.Text = _animationLoop ? "Loop on" : "Loop off";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_frameCount <= 1)
        {
            return;
        }

        if (_isAnimationPlaying)
        {
            StopAnimation();
            return;
        }

        if (_frameIndex + 1 >= _frameCount)
        {
            _frameIndex = 0;
        }
        _isAnimationPlaying = true;
        UpdateAnimationControls();
        ScheduleNextFrame();
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _animationLoop = !_animationLoop;
        UpdateAnimationControls();
    }

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = _directory?.HasPrevious == true;
        NextButton.IsEnabled = _directory?.HasNext == true;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MovePreviousAsync, "Could not open image");
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MoveNextAsync, "Could not open image");
    }

    private async void PreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MovePreviousAsync, "Could not open image");
    }

    private async void NextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MoveNextAsync, "Could not open image");
    }

    private void ZoomInAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_zoom * ZoomStep);
    }

    private void ZoomOutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_zoom / ZoomStep);
    }

    private void ActualSizeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ApplyActualSize();
    }

    private void FitAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ResetView();
    }

    private void RotateAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        RotateClockwise();
    }

    private void PlayPauseAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_frameCount <= 1)
        {
            return;
        }
        args.Handled = true;
        TogglePlayPause();
    }

    private void EscapeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isFullScreen)
        {
            return;
        }
        args.Handled = true;
        SetFullScreen(false);
    }

    private void FullScreenAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetFullScreen(!_isFullScreen);
    }

    private async void CopyImageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(CopyCurrentImageAsync, "Could not copy image");
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
        await RunCommandAsync(SaveAsAsync, "Could not save image");
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

        var point = e.GetCurrentPoint(PanThumb);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // Precision touchpad pinch arrives as Ctrl+wheel, so anchoring the
        // wheel zoom at the pointer makes pinch feel centered on the fingers.
        ZoomAtPoint(delta > 0 ? _zoom * 1.1 : _zoom / 1.1, point.Position);
        e.Handled = true;
    }

    private void PanThumb_TouchPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            return;
        }

        _touchPoints[e.Pointer.PointerId] = e.GetCurrentPoint(PanThumb).Position;
        UpdatePinchState();
    }

    private void PanThumb_TouchMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch ||
            !_touchPoints.ContainsKey(e.Pointer.PointerId))
        {
            return;
        }

        _touchPoints[e.Pointer.PointerId] = e.GetCurrentPoint(PanThumb).Position;
        if (!_isPinching || _imagePixelWidth == 0)
        {
            return;
        }

        if (!TryGetPinchGeometry(out var center, out var distance) ||
            distance < 1 || _pinchDistance < 1)
        {
            return;
        }

        // The focal point follows the fingers, so pan with it and scale
        // around it; the thumb drag is suppressed while pinching.
        ImageTransform.TranslateX += center.X - _pinchCenter.X;
        ImageTransform.TranslateY += center.Y - _pinchCenter.Y;
        ZoomAtPoint(_zoom * (distance / _pinchDistance), center);
        _pinchCenter = center;
        _pinchDistance = distance;
    }

    private void PanThumb_TouchReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_touchPoints.Remove(e.Pointer.PointerId))
        {
            UpdatePinchState();
        }
    }

    private void UpdatePinchState()
    {
        if (TryGetPinchGeometry(out var center, out var distance))
        {
            _isPinching = true;
            _pinchCenter = center;
            _pinchDistance = distance;
        }
        else
        {
            _isPinching = false;
            _pinchDistance = 0;
        }
    }

    /// <summary>
    /// Reports the midpoint and distance of the first two touch contacts.
    /// Extra contacts beyond two are ignored.
    /// </summary>
    private bool TryGetPinchGeometry(out Point center, out double distance)
    {
        center = default;
        distance = 0;
        if (_touchPoints.Count < 2)
        {
            return false;
        }

        var first = default(Point);
        var second = default(Point);
        var index = 0;
        foreach (var point in _touchPoints.Values)
        {
            if (index == 0)
            {
                first = point;
            }
            else
            {
                second = point;
                break;
            }

            index++;
        }

        center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        return true;
    }

    /// <summary>
    /// Scales the image around an anchor point so the content under the
    /// fingers or the cursor stays put. The image scales around its center
    /// and then pans, so the pan offset compensates for the difference the
    /// scale change introduces at the anchor, expressed in the pre-scale
    /// space (anchor - center - pan).
    /// </summary>
    private void ZoomAtPoint(double zoom, Point anchor)
    {
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        var deltaX = anchor.X - PanThumb.ActualWidth / 2 - ImageTransform.TranslateX;
        var deltaY = anchor.Y - PanThumb.ActualHeight / 2 - ImageTransform.TranslateY;
        var factor = 1 - newZoom / _zoom;
        ImageTransform.TranslateX += factor * deltaX;
        ImageTransform.TranslateY += factor * deltaY;
        SetZoom(newZoom);
    }

    private void PanThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_imagePixelWidth == 0 || _isPinching)
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
        RotateClockwise();
    }

    private void RotateClockwise()
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
        ApplyActualSize();
    }

    private void ApplyActualSize()
    {
        var ratio = FitToPixelRatio;
        if (ratio <= 0)
        {
            return;
        }

        // 1:1 must be exact even when the needed scale falls outside the
        // interactive wheel limits; those limits only bound wheel zooming.
        _zoom = 1.0 / ratio;
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
        _ = UpgradeToFullResolutionAsync();
    }

    private double FitToPixelRatio
    {
        get
        {
            if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0 ||
                ImageView.ActualWidth <= 0 || ImageView.ActualHeight <= 0)
            {
                return 1.0;
            }

            // Stretch=Uniform fits the bitmap on its limiting axis. Using the
            // control width alone overestimates the fit scale for tall images,
            // which made 1:1 and the zoom percentage wrong.
            var fitScale = Math.Min(
                ImageView.ActualWidth / _imagePixelWidth,
                ImageView.ActualHeight / _imagePixelHeight);
            var rasterizationScale = Viewport.XamlRoot?.RasterizationScale ?? 1.0;
            return fitScale * rasterizationScale;
        }
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        UpdateViewStatus();

        if (_zoom > 1.001)
        {
            // Zooming past fit magnifies a preview bitmap, so upgrade to the
            // real pixels once. The call is a no-op for full-resolution images.
            _ = UpgradeToFullResolutionAsync();
        }
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
        var frame = _frameCount > 1
            ? $" | {_frameIndex + 1}/{_frameCount}"
            : "";

        if (Math.Abs(_zoom - 1.0) < 0.001)
        {
            ViewStatusText.Text = $"Fit | {rotation:0} deg{frame}{pan}";
            return;
        }

        var percent = _zoom * FitToPixelRatio * 100.0;
        ViewStatusText.Text = $"{Math.Round(percent):0}% | {rotation:0} deg{frame}{pan}";
    }

    private void UpdateFileStatus(string path)
    {
        var name = Path.GetFileName(path);
        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var dimensions = $"{_imagePixelWidth} x {_imagePixelHeight}";
        FileStatusText.Text = string.IsNullOrEmpty(format)
            ? $"{name} | {dimensions}"
            : $"{name} | {dimensions} | {format}";
    }

    private string? CurrentPath => _directory?.CurrentPath;

    private void SetViewControlsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RotateButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
        CopyImageMenuItem.IsEnabled = enabled;
        CopyPathMenuItem.IsEnabled = enabled;
        SaveAsMenuItem.IsEnabled = enabled;
        ShowInExplorerMenuItem.IsEnabled = enabled;
        FitMenuItem.IsEnabled = enabled;
        ActualSizeMenuItem.IsEnabled = enabled;
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(ActionsButton);
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    // Ctrl+, opens the settings window, or closes it when it is already open.
    private void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_settingsWindow is not null)
        {
            CloseSettings();
        }
        else
        {
            OpenSettings();
        }
        args.Handled = true;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, ApplyTheme, _settings.Theme, Handle);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;

        // The window opens at 80% of the viewer's current size and is centered
        // over it, so both windows land on the same monitor and read as one
        // task instead of a stray dialog on another screen.
        var mainPosition = AppWindow.Position;
        var mainSize = AppWindow.Size;
        if (mainSize.Width > 0 && mainSize.Height > 0)
        {
            var settingsWidth = (int)Math.Round(mainSize.Width * SettingsSizeRatio);
            var settingsHeight = (int)Math.Round(mainSize.Height * SettingsSizeRatio);
            window.AppWindow.Resize(new SizeInt32(settingsWidth, settingsHeight));
            window.AppWindow.Move(new PointInt32(
                mainPosition.X + (mainSize.Width - settingsWidth) / 2,
                mainPosition.Y + (mainSize.Height - settingsHeight) / 2));
        }

        window.Activate();
    }

    private void CloseSettings()
    {
        _settingsWindow?.Close();
    }

    private void ApplyTheme(AppTheme theme)
    {
        // System means "no override", so the OS theme flows through. Explicit
        // choices pin the root element, which makes the local Dark/Light
        // dictionaries in App.xaml take effect. The settings window pins its
        // own root so both windows follow the same choice.
        ThemeApplication.Apply(RootGrid, theme);
        _settingsWindow?.ApplyTheme(theme);
    }

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
        await RunCommandAsync(CopyCurrentImageAsync, "Could not copy image");
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
                ShowError(
                    "The current image could not be decoded for copying.",
                    "Could not copy image");
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
            ShowError("Failed to copy image: " + ex.Message, "Could not copy image");
        }
    }

    /// <summary>
    /// Decodes the current frame again as straight (non premultiplied) BGRA,
    /// which is what an encoder and the clipboard expect.
    /// </summary>
    private async Task<SoftwareBitmap?> DecodeStraightBitmapAsync(string path, uint frameIndex)
    {
        var context = _routerContext;
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

    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(SaveAsAsync, "Could not save image");
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
            await Task.Run(() => SaveFrame(context, path, frameIndex, target));
        }
        catch (Exception ex)
        {
            ShowError("Failed to save image: " + ex.Message, "Could not save image");
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
            throw new InvalidOperationException(DescribeStatus(status));
        }

        var image = decoded.Value;
        var encodeStatus = RouterApi.EncodeToFile(context, targetPath, ref image);
        if (encodeStatus != 0)
        {
            throw new InvalidOperationException(DescribeEncodeStatus(encodeStatus));
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

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen)
        {
            return;
        }

        if (fullScreen)
        {
            _wasMaximizedBeforeFullScreen =
                AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Maximized;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _isFullScreen = true;
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _isFullScreen = false;
            if (_wasMaximizedBeforeFullScreen &&
                AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        UpdateCaptionIcons();
    }

    /// <summary>
    /// Runs a UI command and turns an unexpected exception into the inline
    /// error panel. Every fire-and-forget command funnels through here so an
    /// async void handler cannot take the process down.
    /// </summary>
    private async Task RunCommandAsync(Func<Task> command, string title)
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, title);
        }
    }

    private void ShowError(string message, string title = "Could not open image")
    {
        ErrorTitle.Text = title;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string DescribeEncodeStatus(int status)
    {
        return status switch
        {
            RouterApi.ErrUnsupportedFormat => "The target format cannot be encoded.",
            RouterApi.ErrEncodeFailed => "The image could not be encoded.",
            RouterApi.ErrOutOfMemory => "Not enough memory to encode this image.",
            RouterApi.ErrIo => "The file could not be written.",
            _ => $"The image could not be saved (status {status})."
        };
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
