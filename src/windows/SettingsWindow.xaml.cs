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
/// Settings in a window of its own. The main window does not own this window,
/// so it keeps its own taskbar button and activates, minimizes and closes
/// independently; only the settings values and the theme callbacks are shared.
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

        // WinAppSDK hands a new window over in a maximized state and drops an
        // AppWindow resize until the content has loaded, so the first open used
        // to flash a nearly full screen window before snapping to the settings
        // size. Restoring the state and applying the rectangle through Win32
        // happens before that frame is drawn.
        var initialHandle = Handle;
        Win32Window.Restore(initialHandle);
        var initialScale = Win32Window.GetScaleFactor(initialHandle);
        var initialBounds = ComputeBounds(initialScale);
        Win32Window.SetBounds(
            initialHandle, initialBounds.X, initialBounds.Y, initialBounds.Width, initialBounds.Height);

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
        if (Win32Window.IsMinimized(Handle) &&
            AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
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
            var bounds = ComputeBounds(scale);
            AppWindow.MoveAndResize(bounds);
            ApplyMinimumSize(scale);

            // A window that was never moved does not raise a change event, so
            // the rectangle it opened with is recorded here; otherwise closing
            // right away would have nothing to remember.
            _lastBounds = bounds;
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

    private void ApplyMinimumSize(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(PreferredMinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(PreferredMinimumHeight * scale);
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
        _lastBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);

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
        // A maximized window fills the work area, which would overwrite the size
        // the user chose before maximizing. The state flags cannot tell the two
        // apart reliably on every desktop, so the size is compared instead: the
        // configured window never grows past 92% of the work area.
        if (_lastBounds.Width <= 0 || _lastBounds.Height <= 0)
        {
            return;
        }

        if (_lastBounds.Width >= _anchorWorkArea.Width ||
            _lastBounds.Height >= _anchorWorkArea.Height)
        {
            return;
        }

        _settings.SetSettingsWindowBounds(new WindowBounds(
            _lastBounds.X, _lastBounds.Y, _lastBounds.Width, _lastBounds.Height));
    }

    private nint Handle => WindowNative.GetWindowHandle(this);
}
