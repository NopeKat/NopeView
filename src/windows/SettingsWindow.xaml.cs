using Microsoft.UI.Composition.SystemBackdrops;
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
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
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

        // The window is sized after its content loads: a resize applied before
        // the first show is dropped, and the window would open at the framework
        // default instead of the intended settings size.
        if (!_sized)
        {
            _sized = true;
            ConfigureWindow(_anchorWorkArea);
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

    private void ConfigureWindow(RectInt32 workArea)
    {
        var scale = Win32Window.GetScaleFactor(Handle);

        // Open near the middle of the display that hosts the main window, and
        // never larger than that display can hold.
        var width = Math.Min(
            (int)Math.Round(PreferredWidth * scale),
            (int)Math.Round(workArea.Width * WorkAreaRatio));
        var height = Math.Min(
            (int)Math.Round(PreferredHeight * scale),
            (int)Math.Round(workArea.Height * WorkAreaRatio));

        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth =
                (int)Math.Round(PreferredMinimumWidth * scale);
            presenter.PreferredMinimumHeight =
                (int)Math.Round(PreferredMinimumHeight * scale);
        }
    }

    private nint Handle => WindowNative.GetWindowHandle(this);
}
