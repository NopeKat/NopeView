// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NopeView.App.Native;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NopeView.App;

// Window shell: construction, lifecycle, startup sizing, and shared helpers.
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

    // The caption group is centered between the product name and the system
    // buttons. The left guard clears the icon and the name; the spacing mirrors
    // the gap between the file readout and the view readout.
    private const double CaptionStatusLeftGuard = 150;
    private const double CaptionStatusSpacing = 24;

    private readonly DispatcherTimer _animationTimer = new();

    // The full-resolution decode ends with one large bitmap upload, so it is
    // scheduled to run once the zoom input stops instead of inside the gesture.
    private readonly DispatcherTimer _upgradeTimer = new();

    private nint _windowHandle;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;

    // Large images are decoded once at a reduced size for display. The preview
    // long edge follows the viewport in physical pixels, clamped to these
    // bounds. A full decode replaces it when the view magnifies the pixels.
    private const int PreviewMinEdge = 1024;
    private const int PreviewMaxEdge = 4096;

    // Animated frames are decoded again for every step, so their preview is
    // capped tighter than a still image: past a certain size each frame costs
    // more than the display can absorb, and the extra pixels are invisible.
    private const int AnimationPreviewMaxEdge = 2048;

    // Full-resolution decode, clipboard copy and save as all keep the original
    // pixels so the result is not visibly softer. A file past this many pixels
    // is processed at the largest size that fits instead, because the decode
    // buffer alone would otherwise run into gigabytes.
    private const int FullResolutionMegapixels = 64;
    private const long FullResolutionPixelCeiling = 64L * 1000 * 1000;

    // The router loads every format library it finds, which is disk and loader
    // work. It runs on a worker thread from construction on, and the first
    // open awaits the result instead of loading it while the window is shown.
    private readonly Task<RouterSession> _routerSetup;
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

    // One frame is decoded ahead while the current frame is on screen, so a
    // step only hands pixels to the display. The token drops a decode that
    // playback no longer wants, which keeps at most one extra bitmap alive.
    private DecodedFrame? _prefetchedFrame;
    private string? _prefetchedPath;
    private uint _prefetchedIndex;
    private int _prefetchedEdge;
    private int _prefetchToken;

    // Frames are scheduled on the display's refresh grid. The compositor reports
    // every presented frame, so its interval is measured instead of assumed to
    // be 60 Hz; without that, a delay that falls between two refresh ticks holds
    // the previous frame for an extra tick and shows as a stutter.
    private double _refreshPeriodMs = 1000.0 / 60.0;
    private double _lastRenderingMs;
    private readonly Stopwatch _renderingClock = new();

    // The interaction last seen, so a dropped frame can be attributed to the
    // action that was running. The tag lapses to idle when nothing drives the
    // view, which keeps hover and menu animation gaps out of the same bucket.
    private const long InteractionTagHoldMs = 400;
    private string _interactionTag = "idle";
    private long _interactionUntil;

    // Large uploads and page builds only run once the hand has been off the
    // view for a moment. This timestamp is kept even when sampling is off,
    // because the gate is not a diagnostic.
    private const long InputIdleMs = 200;
    private long _lastInputMs;

    /// <summary>
    /// True once no pan, wheel, press or resize has reached the view for a
    /// moment, which is when a large upload or a page build goes unnoticed.
    /// </summary>
    private bool IsInputIdle =>
        Environment.TickCount64 - _lastInputMs >= InputIdleMs;

    // The state the animation commands already show. Frames of one animation
    // share it, so the icon and labels are only rebuilt when it changes
    // instead of on every step.
    private (bool Animated, bool Playing, bool Loop)? _animationControlsState;

    private bool _isFullScreen;
    private bool _wasMaximizedBeforeFullScreen;

    // The rectangle the window returns to when it is un-maximized, and whether
    // it should open maximized again. Both are seeded at startup and kept
    // current while the window is an ordinary overlapped window, then written
    // back on close so the next session reopens where this one ended.
    private RectInt32 _restoreBounds;
    private bool _restoreMaximized = true;

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
    private readonly SettingsService _settings;

    // The settings service is internal to the app, so the window is created
    // through its own assembly only.
    internal MainWindow(SettingsService settings)
    {
        var xamlClock = UiPerf.Start();
        InitializeComponent();
        UiPerf.Stop(xamlClock, "ctor-xaml", "initialize component");

        // The settings file is read once, before the framework starts, so the
        // window only consumes the values it was handed.
        _settings = settings;

        _routerSetup = Task.Run(() =>
        {
            var clock = UiPerf.Start();
            var session = CreateRouterSession();
            UiPerf.Stop(clock, "router-setup", $"{session.Extensions.Count} formats");
            return session;
        });

        // The project keeps Mica and acrylic off, so the window paints itself:
        // the title bar and the content layer share one layer fill and there is
        // no system backdrop behind them.

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
        _upgradeTimer.Tick += UpgradeTimer_Tick;
        _upgradeTimer.Interval = TimeSpan.FromMilliseconds(300);
        _frameSizeTimer.Tick += FrameSizeTimer_Tick;
        _frameSizeTimer.Interval = TimeSpan.FromMilliseconds(FrameSizeBakeDelayMs);
        _settingsPrewarmTimer.Tick += SettingsPrewarmTimer_Tick;
        _settingsPrewarmTimer.Interval =
            TimeSpan.FromMilliseconds(SettingsPrewarmDelayMs);

        // The presented-frame interval only has to be known while something
        // animates, but the handler is cheap and keeps the estimate warm.
        _renderingClock.Start();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        var keysClock = UiPerf.Start();
        AddOemKeyAccelerators();
        AddCommandAccelerators();
        UiPerf.Stop(keysClock, "ctor-accelerators", "key + command");

        var themeClock = UiPerf.Start();
        ApplyThemeEarly(_settings.Theme);
        ApplyImageFrame(_settings.ImageFrame);
        UiPerf.Stop(themeClock, "ctor-theme", "theme + frame");

        var languageClock = UiPerf.Start();
        ApplyLocalization();
        UiPerf.Stop(languageClock, "ctor-localization", "strings");

        // Set the window icon explicitly. For an unpackaged WinUI 3 app the
        // executable's ApplicationIcon only covers Explorer; the taskbar icon,
        // Alt-Tab and thumbnails use the window icon instead, which has to be
        // assigned at runtime through AppWindow.SetIcon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            var iconClock = UiPerf.Start();
            AppWindow.SetIcon(iconPath);
            UiPerf.Stop(iconClock, "ctor-icon", iconPath);
        }

        // Extend content into the title bar so the official TitleBar control
        // occupies the caption area and keeps the system buttons.
        var titleBarClock = UiPerf.Start();
        ExtendsContentIntoTitleBar = true;
        ApplyTitleBarColors(_settings.Theme);
        UiPerf.Stop(titleBarClock, "ctor-title-bar", "extend + colors");

        // System theme changes update ActualTheme without going through
        // ApplyTheme, so the system caption buttons need their own refresh.
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarColors(_settings.Theme);

        var startupClock = UiPerf.Start();
        ConfigureStartupWindow();
        UiPerf.Stop(startupClock, "ctor-startup-window", "placement + maximize");

        // Build the settings window during the first idle stretch, so the first
        // Ctrl+, only has to show a window that already exists.
        _settingsPrewarmTimer.Start();

        Closed += MainWindow_Closed;
        AppWindow.Changed += AppWindow_Changed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        AppWindow.Changed -= AppWindow_Changed;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (_settingsWindow is { } settingsWindow)
        {
            // The window hides on its own close requests, so it needs the
            // permission here before the viewer can go away.
            settingsWindow.AllowClose();
            settingsWindow.Close();
        }
        _animationTimer.Stop();
        _upgradeTimer.Stop();
        _frameSizeTimer.Stop();
        _settingsPrewarmTimer.Stop();
        _decodeCancellation?.Cancel();
        DiscardPrefetch();
        UiPerf.Flush();
        PersistMainWindowBounds();
        _settings.Flush(300);

        // The router may still be loading when the window closes. The session
        // closes itself once the setup finishes, so the native context is
        // released either way.
        _ = _routerSetup.ContinueWith(
            static completed =>
            {
                if (completed.IsCompletedSuccessfully)
                {
                    completed.Result.Close();
                }
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// The router session, created once on a worker thread. Callers await this
    /// instead of touching the native context directly, so a window that opens
    /// before the libraries are loaded simply continues when they are.
    /// </summary>
    private async Task<RouterSession> RouterAsync()
    {
        var session = await _routerSetup;
        _supportedExtensions = session.Extensions;

        if (_closing)
        {
            // Handing out a context that is already being torn down would let a
            // late command decode into freed memory.
            session.Close();
        }

        return session;
    }

    private static RouterSession CreateRouterSession()
    {
        var context = IntPtr.Zero;
        IReadOnlyList<string> extensions = Array.Empty<string>();

        try
        {
            if (RouterApi.TryCreate(null, out var created) && created != IntPtr.Zero)
            {
                context = created;
                extensions = CollectExtensions(created);
            }
        }
        catch (Exception ex)
        {
            // A missing or broken router must not take the window down with it.
            Debug.WriteLine($"Router: could not create the context: {ex.Message}");
            context = IntPtr.Zero;
            extensions = Array.Empty<string>();
        }

        return new RouterSession(context, extensions);
    }

    /// <summary>
    /// Extensions every decodable format claims, built once from the formats
    /// the router discovered. The work belongs to the router setup, so the
    /// window never asks the native side for it while a command runs.
    /// </summary>
    private static IReadOnlyList<string> CollectExtensions(IntPtr context)
    {
        var extensions = new List<string>();

        foreach (var format in RouterApi.ListFormats(context))
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

        return extensions;
    }

    /// <summary>
    /// One created router context and the extensions it supports. Closing is
    /// idempotent and safe from the worker that finished the setup, because the
    /// window may close before the libraries have finished loading.
    /// </summary>
    private sealed class RouterSession
    {
        private readonly object _gate = new();
        private IntPtr _context;
        private bool _closed;

        internal RouterSession(IntPtr context, IReadOnlyList<string> extensions)
        {
            _context = context;
            Extensions = extensions;
        }

        internal IReadOnlyList<string> Extensions { get; }

        internal IntPtr Context
        {
            get
            {
                lock (_gate)
                {
                    return _closed ? IntPtr.Zero : _context;
                }
            }
        }

        internal void Close()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                var context = _context;
                _context = IntPtr.Zero;
                RouterApi.Close(context);
            }
        }
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
    /// Opens the window at the rectangle and maximize state it closed with, or
    /// at the centered default when nothing usable is stored. Running this
    /// before the window is shown means it opens maximized with no visible
    /// resize flash, and the centered or restored size stays as the size an
    /// un-maximize returns to.
    /// </summary>
    private void ConfigureStartupWindow()
    {
        var appWindow = AppWindow;
        var scale = Scale;
        var saved = _settings.MainWindowBounds;

        // A stored rectangle also picks the display the window opens on: the
        // one it was left on, or the primary display when that one is gone.
        // Without a stored rectangle the window follows the display it was
        // created on.
        var display = saved is { } stored
            ? DisplayArea.GetFromRect(
                new RectInt32(stored.X, stored.Y, stored.Width, stored.Height),
                DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;

        var minWidth = (int)Math.Round(PreferredMinimumWidth * scale);
        var minHeight = (int)Math.Round(PreferredMinimumHeight * scale);
        var bounds = saved is { } storedBounds
            ? ClampBounds(storedBounds, workArea, minWidth, minHeight)
            : DefaultBounds(workArea, scale);
        var openMaximized = saved?.Maximized ?? true;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minWidth;
            presenter.PreferredMinimumHeight = minHeight;
        }

        appWindow.MoveAndResize(bounds);
        _restoreBounds = bounds;
        _restoreMaximized = openMaximized;

        if (openMaximized && appWindow.Presenter is OverlappedPresenter maximizedPresenter)
        {
            maximizedPresenter.Maximize();
        }
    }

    /// <summary>
    /// The centered default rectangle used before a window state has ever been
    /// stored. The restore size is capped to a fraction of the work area so
    /// that un-maximizing always produces a visibly smaller window, even on a
    /// display where the preferred size would cover the whole screen.
    /// </summary>
    private static RectInt32 DefaultBounds(RectInt32 workArea, double scale)
    {
        var widthDip = Math.Min(PreferredWidth, workArea.Width / scale * RestoreAreaRatio);
        var heightDip = Math.Min(PreferredHeight, workArea.Height / scale * RestoreAreaRatio);
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        return new RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// Reuses a stored rectangle, but pulls it back inside the work area the
    /// window opens on, so a window left on a monitor that is gone cannot reopen
    /// where nothing can show it. The values are already physical pixels, so
    /// only the bounds are clamped and the size is not scaled again.
    /// </summary>
    private static RectInt32 ClampBounds(
        WindowBounds stored, RectInt32 workArea, int minWidth, int minHeight)
    {
        var width = Math.Clamp(stored.Width, minWidth, Math.Max(minWidth, workArea.Width));
        var height = Math.Clamp(stored.Height, minHeight, Math.Max(minHeight, workArea.Height));
        var x = Math.Clamp(
            stored.X,
            workArea.X,
            Math.Max(workArea.X, workArea.X + workArea.Width - width));
        var y = Math.Clamp(
            stored.Y,
            workArea.Y,
            Math.Max(workArea.Y, workArea.Y + workArea.Height - height));

        return new RectInt32(x, y, width, height);
    }

    /// <summary>
    /// Keeps the rectangle and the maximize state the window would restore to.
    /// A minimized or full-screen window does not report the rectangle the user
    /// left, so only an ordinary overlapped state is recorded.
    /// </summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        if (_isFullScreen ||
            (sender.Presenter is { } windowPresenter &&
                windowPresenter.Kind == AppWindowPresenterKind.FullScreen) ||
            Win32Window.IsMinimized(Handle))
        {
            return;
        }

        var maximized =
            sender.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized;
        _restoreMaximized = maximized;
        if (maximized)
        {
            // A maximized window reports the work area as its own size, so its
            // rectangle is not the one an un-maximize returns to.
            return;
        }

        var position = sender.Position;
        var size = sender.Size;
        if (size.Width > 0 && size.Height > 0)
        {
            _restoreBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        }
    }

    /// <summary>
    /// Stores the rectangle and maximize state the window closes with, so the
    /// next start reopens where this session ended. A window without a usable
    /// rectangle keeps whatever was stored last time instead of writing zeros.
    /// </summary>
    private void PersistMainWindowBounds()
    {
        if (_restoreBounds.Width <= 0 || _restoreBounds.Height <= 0)
        {
            return;
        }

        _settings.SetMainWindowBounds(new WindowBounds(
            _restoreBounds.X, _restoreBounds.Y,
            _restoreBounds.Width, _restoreBounds.Height,
            _restoreMaximized));
    }

    /// <summary>
    /// Takes one request from a later launch: brings this window forward and
    /// opens the file the launch asked for, so a second copy lands here instead
    /// of in a window of its own. The path may be empty, which only asks for
    /// the window. Called on the window thread.
    /// </summary>
    internal void AcceptSingleInstanceRequest(string? path)
    {
        if (_closing)
        {
            return;
        }

        if (Win32Window.IsMinimized(_windowHandle))
        {
            Win32Window.Restore(_windowHandle);
        }

        Activate();
        Win32Window.BringToForeground(_windowHandle);

        if (path is { Length: > 0 })
        {
            _ = RunCommandAsync(() => OpenImageAsync(path), Strings.OpenTitle);
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // The span a user feels as "the app opening": from the process starting
        // to content that can be drawn.
        if (UiPerf.IsEnabled)
        {
            try
            {
                var startTime = System.Diagnostics.Process
                    .GetCurrentProcess()
                    .StartTime;
                UiPerf.Record(
                    "startup-first-frame",
                    DateTime.Now - startTime,
                    _settings.Theme.ToString());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Start time is unavailable for some protected processes; the
                // measurement is dropped rather than guessed.
            }
        }

        // The official TitleBar control owns the drag region, pane toggle and
        // system caption buttons, so it is registered as the title bar directly.
        SetTitleBar(AppTitleBar);

        // The caption is non-client, so its right click becomes WM_NCRBUTTONUP
        // and the shell answers with the classic window menu, and hovering the
        // system buttons makes the shell create a tooltip under them. Both are
        // handled by message subclasses, and the subclasses have to be applied
        // after the title bar registers its own so ours sees the messages first.
        WindowMessages.SuppressCaptionChrome(_windowHandle);

        ConfigureErrorBar();
        UpdateCaptionButtonInset();
        AppTitleBar.SizeChanged += (_, _) => UpdateCaptionButtonInset();
        ViewStatusText.SizeChanged += (_, _) => UpdateCaptionButtonInset();

        // The caption buttons keep the system palette until this runs. They are
        // only colored after the first layout, because the first assignment
        // initializes the title bar customization and costs about 60 ms, which
        // would otherwise sit in front of the first paint.
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => ApplyTitleBarColors(_settings.Theme));

        OpenImageFromCommandLine();
    }

    /// <summary>
    /// The caption readouts are centered as one group, so the group has to stay
    /// within the span that runs from the product name to the system buttons.
    /// The right guard follows the real DIP inset, which the title bar template
    /// scales wrong; the left guard covers the icon and the product name.
    /// </summary>
    private void UpdateCaptionButtonInset()
    {
        var inset = AppWindow.TitleBar.RightInset / Scale;
        var available = RootGrid.ActualWidth - CaptionStatusLeftGuard - inset - 8;

        CaptionStatus.MaxWidth = System.Math.Max(0, available);
        FileStatusText.MaxWidth = System.Math.Max(
            0,
            available - ViewStatusText.ActualWidth - CaptionStatusSpacing);
    }

    private string? CurrentPath => _directory?.CurrentPath;
}
