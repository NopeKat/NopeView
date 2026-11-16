using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NopeView.App.Native;
using Windows.Graphics;
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

    private bool _titleBarInitialized;

    public MainWindow()
    {
        InitializeComponent();

        // The app draws its own title bar row; the system caption buttons
        // overlay it with transparent backgrounds. This also forwards to
        // AppWindowTitleBar, so the title bar options are not set twice.
        ExtendsContentIntoTitleBar = true;

        ConfigureStartupWindow();
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
            // Keep the native caption buttons at their standard height.
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

            // Explicitly drop the caption icon and its system menu entry. Leaving
            // this to the extended title bar alone relies on undocumented layout
            // behavior, which could change between Windows App SDK versions.
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }

        // Register the custom row as the drag region; the caption buttons keep
        // overlaying the top-right corner.
        SetTitleBar(AppTitleBar);

        // Match the row to the real caption strip. XAML carries the design-time
        // default, so this is skipped while the height is still unavailable.
        var captionHeight = AppWindow.TitleBar.Height;
        if (captionHeight > 0)
        {
            AppTitleBar.Height = captionHeight / Scale;
        }

        UpdateTitleBarTheme();

        // The native system menu is unwanted on the custom title bar. Filter the
        // messages that open it instead of clearing WS_SYSMENU, which would also
        // shift the caption buttons off the visible right edge.
        Win32Window.SuppressSystemMenu(Handle);
    }

    /// <summary>
    /// The system-drawn caption buttons do not follow the app theme on their
    /// own, so their colors are applied explicitly. Everything the system would
    /// paint behind them stays transparent so the custom row shows through.
    /// </summary>
    private void UpdateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;

        var foreground = dark ? Colors.White : Colors.Black;
        var inactive = dark
            ? ColorHelper.FromArgb(255, 128, 128, 128)
            : ColorHelper.FromArgb(255, 153, 153, 153);
        var hover = dark
            ? ColorHelper.FromArgb(60, 255, 255, 255)
            : ColorHelper.FromArgb(45, 0, 0, 0);

        bar.BackgroundColor = Colors.Transparent;
        bar.ForegroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveForegroundColor = inactive;
    }
}
