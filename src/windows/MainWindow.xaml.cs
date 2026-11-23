using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    // The smallest window that still fits the command layer on one row and a
    // settings card side by side. It follows the small breakpoint in the design
    // guidance, so layouts are never forced into the cramped case.
    private const int PreferredMinimumWidth = 640;
    private const int PreferredMinimumHeight = 480;

    private readonly DispatcherTimer _animationTimer = new();

    private nint _windowHandle;
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
    private readonly ViewState _view = new();
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    // Touch contacts currently on the viewport. Two or more turn the gesture
    // into a pinch, which scales and pans around the contact midpoint while
    // pointer panning is suppressed.
    private readonly Dictionary<uint, Point> _touchPoints = new();
    private Point _pinchCenter;
    private double _pinchDistance;
    private bool _isPinching;

    // The pointer currently dragging the image. Pointer capture keeps the
    // drag alive after the cursor leaves the viewport.
    private bool _isPanning;
    private uint _panPointerId;
    private Point _lastPanPoint;

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
    // instead of letting it overwrite the newer image. The gate keeps only one
    // native decode in flight; the token cancels requests still waiting for it.
    private int _decodeGeneration;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _decodeCancellation;

    // A single source is reused for every frame. Creating a source per frame
    // allocates a new native image object for each animation step.
    private readonly SoftwareBitmapSource _frameSource = new();

    // User preferences are loaded at startup and saved as soon as a choice is
    // made on the settings page. Theme is applied to the root grid so the local
    // ThemeDictionaries in App.xaml switch without touching system brushes.
    private readonly SettingsService _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        // Mica is the Fluent window backdrop. The content layer uses a low
        // opacity fill so the material shows through the title bar and the
        // viewer surface, which keeps both areas in one continuous plane.
        SystemBackdrop = new MicaBackdrop();

        // The viewport receives pointer events for panning and touch pinch
        // tracking, including events already handled by child elements.
        Viewport.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Viewport_PointerPressed),
            true);
        Viewport.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(Viewport_PointerMoved),
            true);
        Viewport.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(Viewport_PointerReleased),
            true);
        Viewport.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(Viewport_PointerReleased),
            true);
        Viewport.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(Viewport_PointerReleased),
            true);
        _windowHandle = Handle;
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(100);
        AddOemKeyAccelerators();
        AddCommandAccelerators();

        if (!RouterApi.TryCreate(null, out _routerContext))
        {
            _routerContext = IntPtr.Zero;
        }

        _settings.Load();
        Localization.Apply(_settings.Language);
        ApplyTheme(_settings.Theme);
        ApplyImageFrame(_settings.ImageFrame);
        ApplyLocalization();

        // Set the window icon explicitly. For an unpackaged WinUI 3 app the
        // executable's ApplicationIcon alone does not reach Explorer, so the
        // taskbar button, Alt-Tab and thumbnails show a default icon until the
        // running window is given one through AppWindow.SetIcon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // Extend content into the title bar so the official TitleBar control
        // occupies the caption area and keeps the system buttons.
        ExtendsContentIntoTitleBar = true;
        ApplyTitleBarColors(_settings.Theme);

        // System theme changes update ActualTheme without going through
        // ApplyTheme, so the system caption buttons need their own refresh.
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarColors(_settings.Theme);

        ConfigureStartupWindow();

        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Adds the accelerators for keys that Windows.System.VirtualKey does not
    /// name. The main keyboard's '+' and '-' are OEM keys, so they exist only
    /// as raw virtual-key codes and cannot be declared in XAML.
    /// </summary>
    private void AddOemKeyAccelerators()
    {
        AddAccelerator(
            ZoomInButton,
            0xBB,
            Windows.System.VirtualKeyModifiers.None,
            ZoomInAccelerator_Invoked);
        AddAccelerator(
            ZoomInButton,
            0xBB,
            Windows.System.VirtualKeyModifiers.Shift,
            ZoomInAccelerator_Invoked);
        AddAccelerator(
            ZoomOutButton,
            0xBD,
            Windows.System.VirtualKeyModifiers.None,
            ZoomOutAccelerator_Invoked);
        AddAccelerator(
            RootGrid,
            0xBC,
            Windows.System.VirtualKeyModifiers.Control,
            SettingsAccelerator_Invoked,
            enableHint: false);
    }

    /// <summary>
    /// Commands own their shortcuts. This keeps the shortcut text in the
    /// button tooltip, the way the official Fluent command surfaces do, while
    /// the root only keeps keys that have no visible command.
    /// </summary>
    private void AddCommandAccelerators()
    {
        EnableAcceleratorHints(OpenButton);
        AddAccelerator(
            PreviousButton,
            (int)Windows.System.VirtualKey.Left,
            Windows.System.VirtualKeyModifiers.None,
            PreviousAccelerator_Invoked);
        AddAccelerator(
            NextButton,
            (int)Windows.System.VirtualKey.Right,
            Windows.System.VirtualKeyModifiers.None,
            NextAccelerator_Invoked);
        AddAccelerator(
            PlayPauseButton,
            (int)Windows.System.VirtualKey.Space,
            Windows.System.VirtualKeyModifiers.None,
            PlayPauseAccelerator_Invoked);
        AddAccelerator(
            ZoomInButton,
            (int)Windows.System.VirtualKey.Add,
            Windows.System.VirtualKeyModifiers.None,
            ZoomInAccelerator_Invoked);
        AddAccelerator(
            ZoomOutButton,
            (int)Windows.System.VirtualKey.Subtract,
            Windows.System.VirtualKeyModifiers.None,
            ZoomOutAccelerator_Invoked);
        AddAccelerator(
            RotateRightButton,
            (int)Windows.System.VirtualKey.R,
            Windows.System.VirtualKeyModifiers.None,
            RotateRightAccelerator_Invoked);
        AddAccelerator(
            RotateLeftButton,
            (int)Windows.System.VirtualKey.R,
            Windows.System.VirtualKeyModifiers.Shift,
            RotateLeftAccelerator_Invoked);
        AddAccelerator(
            FitButton,
            (int)Windows.System.VirtualKey.F,
            Windows.System.VirtualKeyModifiers.None,
            FitAccelerator_Invoked);
        AddAccelerator(
            ActualSizeButton,
            (int)Windows.System.VirtualKey.Number0,
            Windows.System.VirtualKeyModifiers.None,
            ActualSizeAccelerator_Invoked);
    }

    private static void EnableAcceleratorHints(FrameworkElement target)
    {
        target.KeyboardAcceleratorPlacementMode =
            KeyboardAcceleratorPlacementMode.Auto;
    }

    private void AddAccelerator(
        FrameworkElement target,
        int key,
        Windows.System.VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler,
        bool enableHint = true)
    {
        if (enableHint)
        {
            EnableAcceleratorHints(target);
        }

        var accelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)key,
            Modifiers = modifiers
        };
        accelerator.Invoked += handler;
        target.KeyboardAccelerators.Add(accelerator);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _settingsWindow?.Close();
        _animationTimer.Stop();
        _decodeCancellation?.Cancel();
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
            presenter.PreferredMinimumWidth =
                (int)Math.Round(PreferredMinimumWidth * scale);
            presenter.PreferredMinimumHeight =
                (int)Math.Round(PreferredMinimumHeight * scale);
            presenter.Maximize();
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // The official TitleBar control owns the drag region, pane toggle and
        // system caption buttons, so it is registered as the title bar directly.
        SetTitleBar(AppTitleBar);
        ConfigureOverflowButton();
        ConfigureErrorBar();
        UpdateCaptionButtonInset();
        AppTitleBar.SizeChanged += (_, _) => UpdateCaptionButtonInset();
        OpenImageFromCommandLine();
    }

    /// <summary>
    /// Keeps the caption readout just left of the system buttons. The title bar
    /// template sizes its own caption button padding from the pixel inset, so
    /// the line is placed outside the template and pinned to the real DIP inset.
    /// </summary>
    private void UpdateCaptionButtonInset()
    {
        var inset = AppWindow.TitleBar.RightInset / Scale;
        ViewStatusText.Margin = new Thickness(0, 0, inset + 8, 0);
    }

    /// <summary>
    /// The error bar arrives with a stock template whose close command carries
    /// the framework's own text. The command is renamed from the app resources
    /// so it matches the language of the window.
    /// </summary>
    private void ConfigureErrorBar()
    {
        ErrorPanel.ApplyTemplate();

        Button? closeButton = FindDescendant<Button>(ErrorPanel, "CloseButton");
        if (closeButton is not null)
        {
            var close = Localization.Get("Action.Close");
            AutomationProperties.SetName(closeButton, close);
            ToolTipService.SetToolTip(closeButton, close);
        }

        // The glyph is decorative: the title and the message already describe
        // what happened, so it stays out of the reading order.
        TextBlock? icon = FindDescendant<TextBlock>(ErrorPanel, "Icon")
            ?? FindDescendant<TextBlock>(ErrorPanel, "StandardIcon");
        if (icon is not null)
        {
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        }
    }

    private void ConfigureOverflowButton()
    {
        // CommandBar owns the overflow button but does not expose it as a
        // named part. Keep the established automation contract by decorating
        // the official button instead of drawing a second one.
        ViewerCommandBar.ApplyTemplate();
        ViewerCommandBar.UpdateLayout();
        Button? moreButton = FindDescendant<Button>(ViewerCommandBar, "MoreButton");
        if (moreButton is not null)
        {
            var more = Localization.Get("Action.More");
            AutomationProperties.SetAutomationId(moreButton, "ActionsButton");
            AutomationProperties.SetName(moreButton, more);
            ToolTipService.SetToolTip(moreButton, more);
        }
    }

    /// <summary>
    /// Applies the current language to every string this window owns. The
    /// window carries no hard coded text, so this runs on first load and again
    /// after a switch, without rebuilding the window.
    /// </summary>
    private void ApplyLocalization()
    {
        // A failure message in the previous language would be stale, so the
        // bar closes on a switch instead of mixing two languages.
        ErrorPanel.IsOpen = false;
        ErrorPanel.Title = Localization.Get("ErrorPanel.Title");

        EmptyStateTitle.Text = Localization.Get("EmptyStateTitle.Text");
        EmptyStateHint.Text = Localization.Get("EmptyStateHint.Text");
        EmptyOpenButton.Content = Localization.Get("EmptyOpenButton.Content");

        OpenButton.Label = Localization.Get("OpenButton.Label");
        PreviousButton.Label = Localization.Get("PreviousButton.Label");
        NextButton.Label = Localization.Get("NextButton.Label");
        ZoomOutButton.Label = Localization.Get("ZoomOutButton.Label");
        ZoomInButton.Label = Localization.Get("ZoomInButton.Label");
        RotateLeftButton.Label = Localization.Get("RotateLeftButton.Label");
        RotateRightButton.Label = Localization.Get("RotateRightButton.Label");
        FlipHorizontalButton.Label = Localization.Get("FlipHorizontalButton.Label");
        FlipVerticalButton.Label = Localization.Get("FlipVerticalButton.Label");
        FitButton.Label = Localization.Get("FitButton.Label");
        // The command shows a numeric scale label instead of a glyph, so the
        // accessible name and tooltip carry the localized wording.
        string actualSizeLabel = Localization.Get("ActualSizeButton.Label");
        AutomationProperties.SetName(ActualSizeButton, actualSizeLabel);
        ToolTipService.SetToolTip(ActualSizeButton, actualSizeLabel);
        PlayPauseButton.Label = Localization.Get(
            _isAnimationPlaying ? "Action.Pause" : "Action.Play");
        LoopButton.Label = Localization.Get(
            _animationLoop ? "Action.LoopOn" : "Action.LoopOff");

        CopyFileMenuItem.Text = Localization.Get("ContextCopyFile.Text");
        CopyImageMenuItem.Text = Localization.Get("ContextCopyImage.Text");
        CopyPathMenuItem.Text = Localization.Get("ContextCopyPath.Text");
        SaveAsMenuItem.Text = Localization.Get("ContextSaveAs.Text");
        ShowInExplorerMenuItem.Text = Localization.Get("ContextShowInExplorer.Text");
        FitMenuItem.Text = Localization.Get("ContextFit.Text");
        ActualSizeMenuItem.Text = Localization.Get("ContextActualSize.Text");
        SettingsMenuItem.Text = Localization.Get("ContextSettings.Text");
        SettingsCommand.Label = Localization.Get("SettingsCommand.Label");
        AutomationProperties.SetName(
            SettingsCommand,
            Localization.Get("ContextSettings.Text"));

        CopyFileCommand.Label = Localization.Get("CopyFileCommand.Label");
        CopyImageCommand.Label = Localization.Get("CopyImageCommand.Label");
        CopyPathCommand.Label = Localization.Get("CopyPathCommand.Label");
        SaveAsCommand.Label = Localization.Get("SaveAsCommand.Label");
        ShowInExplorerCommand.Label = Localization.Get("ShowInExplorerCommand.Label");

        AutomationProperties.SetName(
            LoadingRing, Localization.Get("Loading.Name"));

        ConfigureErrorBar();
        ConfigureOverflowButton();
        UpdateAnimationControls();
        UpdateViewStatus();
        _settingsWindow?.ApplyLocalization();

        var path = CurrentPath;
        if (path is not null)
        {
            UpdateFileStatus(path);
            UpdateImageAutomationName(path);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            T? match = FindDescendant<T>(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
    private void EscapeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        // Escape leaves full screen. The settings window handles its own Escape
        // and closes itself.
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
            SetChromeVisible(false);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _isFullScreen = false;
            SetChromeVisible(true);
            if (_wasMaximizedBeforeFullScreen &&
                AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>
    /// Full screen is a viewing mode: the caption and the command layer step
    /// aside so the picture owns the whole display. Escape or F11 restores
    /// both, and the caption keeps its system buttons once it is back.
    /// </summary>
    private void SetChromeVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.Visibility = visibility;
        ViewerCommandBar.Visibility = visibility;
        ViewStatusText.Visibility = visibility;
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

    private void ShowError(string message, string? title = null)
    {
        ErrorPanel.Title = title ?? Strings.OpenTitle;
        ErrorPanel.Message = message;
        ErrorPanel.IsOpen = true;
    }
}
