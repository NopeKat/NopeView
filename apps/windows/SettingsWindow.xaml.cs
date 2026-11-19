using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NopeView.App.Native;
using Windows.System;

namespace NopeView.App;

/// <summary>
/// Hosts the settings page in its own top-level window. Mica shows through the
/// transparent root, so the page keeps the same Fluent surface as the main
/// window instead of a flat dialog background. Theme choices are applied to
/// this window first and then forwarded to the viewer window that owns the
/// stored setting.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly Action<AppTheme> _applyThemeToHost;
    private readonly nint _windowHandle;
    private bool _titleBarInitialized;

    internal SettingsWindow(
        SettingsService settings,
        Action<AppTheme> applyThemeToHost,
        AppTheme theme,
        nint ownerHandle)
    {
        InitializeComponent();

        // Content extends into the title bar so the official TitleBar control
        // owns the caption and the system buttons.
        ExtendsContentIntoTitleBar = true;

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Win32Window.SetOwner(_windowHandle, ownerHandle);
        _applyThemeToHost = applyThemeToHost;
        SystemBackdrop = new MicaBackdrop();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        ApplyTheme(theme);
        SettingsFrame.Navigate(
            typeof(SettingsPage),
            new SettingsPageParameter(settings, OnThemeChosen));
        AddCloseAccelerators();
    }

    /// <summary>
    /// Pins the window root to the stored theme. The page and its flyouts pick
    /// the pinned theme up through the element tree.
    /// </summary>
    internal void ApplyTheme(AppTheme theme)
    {
        ThemeApplication.Apply(RootGrid, theme);
    }

    private void OnThemeChosen(AppTheme theme)
    {
        ApplyTheme(theme);
        _applyThemeToHost(theme);
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_titleBarInitialized)
        {
            return;
        }
        _titleBarInitialized = true;

        // Register the official TitleBar control as the drag region. It keeps
        // the system caption buttons instead of a self-drawn close button.
        SetTitleBar(SettingsTitleBar);
    }

    // Escape and Ctrl+, both close the window. Ctrl+, is declared as the raw
    // OEM comma key because it has no named VirtualKey.
    private void AddCloseAccelerators()
    {
        AddCloseAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None);
        AddCloseAccelerator((VirtualKey)0xBC, VirtualKeyModifiers.Control);
    }

    private void AddCloseAccelerator(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }
}
