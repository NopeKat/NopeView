using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NopeView.App;

// Window shell: construction, lifecycle, startup sizing, shared helpers.
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

    private string? CurrentPath => _directory?.CurrentPath;

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(ActionsButton);
        }
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
}
