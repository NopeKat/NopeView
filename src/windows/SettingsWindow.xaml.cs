using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NopeView.App.Native;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace NopeView.App;

/// <summary>
/// Settings in a modal window. The viewer owns it, so it stays above the viewer
/// and has no taskbar button of its own, and the viewer is blocked while it is
/// open. Only the settings values and the theme callbacks are shared.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // A single settings column reads best at this size. The window stays
    // resizable and is clamped to the work area it opens on.
    private const int PreferredWidth = 960;
    private const int PreferredHeight = 720;
    private const int PreferredMinimumWidth = 480;
    private const int PreferredMinimumHeight = 360;

    private const double WorkAreaRatio = 0.92;

    private readonly SettingsService _settings;
    private readonly Action<AppTheme> _applyThemeToApp;
    private readonly SettingsPage _page;
    private readonly RectInt32 _anchorWorkArea;
    private RectInt32 _lastBounds;
    private DispatcherQueueTimer? _boundsTimer;
    private bool _sized;
    private bool _maximized;
    private bool _openMaximized;
    private bool _maximizeOnLoad;

    internal SettingsWindow(
        SettingsService settings,
        Action<AppTheme> applyTheme,
        Action<string> applyLanguage,
        Action<ImageFrameStyle> applyImageFrame,
        RectInt32 anchorWorkArea)
    {
        InitializeComponent();
        _settings = settings;
        _applyThemeToApp = applyTheme;
        _anchorWorkArea = anchorWorkArea;

        // WinAppSDK creates the window at a default rectangle and drops an
        // AppWindow resize until the content has loaded, so the rectangle goes
        // in through Win32 while the window is still hidden. Restoring a window
        // that was handed over maximized shows it, so the rectangle has to be
        // in place first; otherwise a default sized window flashes before the
        // settings size lands. Clearing the state through the presenter is what
        // also turns the caption back into a maximize button instead of a
        // restore one.
        // A window that is meant to open maximized keeps the state it was
        // handed, and one that has to start maximized gets that state while it
        // is still hidden.
        var initialHandle = Handle;
        bool initialMaximized = AppWindow.Presenter is OverlappedPresenter firstPresenter &&
            firstPresenter.State == OverlappedPresenterState.Maximized;

        // A window that was left maximized opens maximized again, and so does
        // every window while the desktop asks applications to open that way.
        // The rectangle below stays the size to restore to.
        bool openMaximized = settings.SettingsWindowBounds is { Maximized: true } ||
            SystemPreferences.MaximizeWindowsByDefault();

        _maximized = openMaximized;
        _openMaximized = openMaximized;
        _maximizeOnLoad = openMaximized && !initialMaximized;

        var initialScale = Win32Window.GetScaleFactor(initialHandle);
        var initialBounds = ComputeBounds(initialScale);

        // The window is still hidden here, and it has to stay hidden until the
        // rectangle is in place: restoring a window shows it, and a show before
        // the rectangle lands flashes a default sized window first. The restore
        // is only needed for a window that was handed over maximized and is not
        // meant to stay that way.
        Win32Window.Hide(initialHandle);

        if (initialMaximized && !openMaximized)
        {
            if (AppWindow.Presenter is OverlappedPresenter initialPresenter)
            {
                initialPresenter.Restore();
            }

            Win32Window.Hide(initialHandle);
        }

        Win32Window.SetBounds(
            initialHandle, initialBounds.X, initialBounds.Y, initialBounds.Width, initialBounds.Height);
        _lastBounds = initialBounds;

        // Maximizing through Win32 applies right away, so the window shows maximized
        // on its first frame. The presenter request waits for the content and would
        // let the normal rectangle appear first. The loaded pass repeats the request
        // in case the window state was dropped on the way.
        if (_maximizeOnLoad)
        {
            Win32Window.Maximize(initialHandle);
        }

        SystemBackdrop = new MicaBackdrop();

        _page = new SettingsPage();
        _page.Initialize(new SettingsPageParameter(
            settings, ApplyTheme, applyLanguage, applyImageFrame));
        SettingsHost.Content = _page;

        // Same chrome as the main window: the official TitleBar occupies the
        // caption area and the layer fill carries the Mica through it.
        ExtendsContentIntoTitleBar = true;
        ApplyLocalization();
        ThemeApplication.Apply(RootGrid, settings.Theme);
        WindowChrome.ApplyTitleBarColors(this, RootGrid, settings.Theme);

        // A system theme change updates ActualTheme without going through
        // ApplyTheme, so this window's caption buttons need their own refresh.
        RootGrid.ActualThemeChanged += (_, _) =>
            WindowChrome.ApplyTitleBarColors(this, RootGrid, _settings.Theme);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // The window keeps its own size and place across sessions, the way a
        // separate window is expected to, so every move is remembered and the
        // last one is written when the window closes.
        AppWindow.Changed += AppWindow_Changed;
        Closed += SettingsWindow_BoundsClosing;
    }

    /// <summary>
    /// Writes the strings this window owns. The page refreshes its own labels
    /// when the language changes, so this only covers the caption.
    /// </summary>
    internal void ApplyLocalization()
    {
        var title = Localization.Get("SettingsHeader.Text");
        Title = title;
        AppTitleBar.Title = title;
        AutomationProperties.SetName(RootGrid, title);
    }

    /// <summary>
    /// Brings the window forward. An already open window is restored instead of
    /// opened a second time, so the app never ends up with two settings windows.
    /// </summary>
    internal void ShowAndFocus()
    {
        // Restoring from the taskbar drops a maximized window back to normal, so
        // a window that is supposed to be maximized is maximized again.
        if (Win32Window.IsMinimized(Handle) &&
            AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            if (_maximized)
            {
                presenter.Maximize();
            }
        }

        Activate();
        _page.FocusDefault();
    }

    private void ApplyTheme(AppTheme theme)
    {
        // The page lives in this window, so the theme is applied here first and
        // then forwarded to the main window. Both captions follow the same
        // palette through WindowChrome.
        ThemeApplication.Apply(RootGrid, theme);
        WindowChrome.ApplyTitleBarColors(this, RootGrid, theme);
        _applyThemeToApp(theme);
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        SetTitleBar(AppTitleBar);

        // The early Win32 sizing in the constructor normally lands before the
        // first frame. This pass repeats it through the windowing API and sets
        // the presenter minimums, which need the content to be loaded.
        if (!_sized)
        {
            _sized = true;
            var scale = Win32Window.GetScaleFactor(Handle);
            var bounds = _lastBounds;
            if (!_openMaximized)
            {
                // A maximized window ignores this: it only moves the rectangle
                // it would restore to, which the constructor already set.
                AppWindow.MoveAndResize(bounds);
            }

            ApplyPresenterLimits(scale);

            if (_maximizeOnLoad &&
                AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // The constructor already asked for this while the window was
                // hidden; the request is repeated here in case the state did
                // not survive the content load.
                presenter.Maximize();
            }
        }
    }

    // Settings has no back stack, so Escape closes the window the way it closes
    // a settings dialog.
    private void EscapeAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private RectInt32 ComputeBounds(double scale)
    {
        var saved = _settings.SettingsWindowBounds;

        // The stored rectangle is already in physical pixels, so it is reused
        // as-is; only the fallback size has to be scaled from the design units.
        var minWidth = (int)Math.Round(PreferredMinimumWidth * scale);
        var minHeight = (int)Math.Round(PreferredMinimumHeight * scale);
        var limitWidth = Math.Max(minWidth, (int)Math.Round(_anchorWorkArea.Width * WorkAreaRatio));
        var limitHeight = Math.Max(minHeight, (int)Math.Round(_anchorWorkArea.Height * WorkAreaRatio));

        var width = Math.Clamp(
            saved?.Width ?? (int)Math.Round(PreferredWidth * scale), minWidth, limitWidth);
        var height = Math.Clamp(
            saved?.Height ?? (int)Math.Round(PreferredHeight * scale), minHeight, limitHeight);

        // A remembered place is kept, but pulled back inside the display that
        // hosts the viewer, so a window dragged onto a monitor that is gone
        // cannot reopen where nothing can show it.
        var x = saved is { } storedX
            ? Math.Clamp(
                storedX.X,
                _anchorWorkArea.X,
                Math.Max(_anchorWorkArea.X, _anchorWorkArea.X + _anchorWorkArea.Width - width))
            : _anchorWorkArea.X + Math.Max(0, (_anchorWorkArea.Width - width) / 2);
        var y = saved is { } storedY
            ? Math.Clamp(
                storedY.Y,
                _anchorWorkArea.Y,
                Math.Max(_anchorWorkArea.Y, _anchorWorkArea.Y + _anchorWorkArea.Height - height))
            : _anchorWorkArea.Y + Math.Max(0, (_anchorWorkArea.Height - height) / 2);

        return new RectInt32(x, y, width, height);
    }

    private void ApplyPresenterLimits(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(PreferredMinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(PreferredMinimumHeight * scale);

            // The window is modal and has no taskbar button, so minimizing it
            // would leave the viewer blocked with nothing on screen to return
            // to. It stays maximizable, because the system can ask for that.
            presenter.IsMinimizable = false;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;

        // The state flags cannot tell a maximized window apart reliably on every
        // desktop, so the size is compared instead: the configured window never
        // grows past 92% of the work area, so filling it means maximized. A
        // maximized window only marks the flag and keeps the rectangle the user
        // chose before maximizing.
        if (size.Width >= _anchorWorkArea.Width || size.Height >= _anchorWorkArea.Height)
        {
            _maximized = true;
        }
        else
        {
            _maximized = false;
            _lastBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        }

        // Dragging fires a change for every step, so the write waits until the
        // user stops moving the window for a moment.
        _boundsTimer ??= CreateBoundsTimer();
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private DispatcherQueueTimer CreateBoundsTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveBounds();
        };
        return timer;
    }

    private void SettingsWindow_BoundsClosing(object sender, WindowEventArgs args)
    {
        _boundsTimer?.Stop();
        SaveBounds();
    }

    private void SaveBounds()
    {
        if (_lastBounds.Width <= 0 || _lastBounds.Height <= 0)
        {
            return;
        }

        _settings.SetSettingsWindowBounds(new WindowBounds(
            _lastBounds.X, _lastBounds.Y, _lastBounds.Width, _lastBounds.Height, _maximized));
    }

    internal nint Handle => WindowNative.GetWindowHandle(this);
}
