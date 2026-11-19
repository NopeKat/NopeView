using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Foundation;

namespace NopeView.App;

// Settings window plumbing: open, close, and theme forwarding.
public sealed partial class MainWindow : Window
{
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
}
