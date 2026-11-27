// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace NopeView.App;

// Command surface: accelerators, full screen, and shared command wrappers.
public sealed partial class MainWindow : Window
{
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
        CaptionStatus.Visibility = visibility;
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
        // The panel keeps whatever severity it last showed, so every error
        // states its own instead of inheriting a warning.
        ErrorPanel.Severity = InfoBarSeverity.Error;
        ErrorPanel.Title = title ?? Strings.OpenTitle;
        ErrorPanel.Message = message;
        SetErrorBarOpen(true);
    }

    /// <summary>
    /// Shows a note about a result the user still gets, but on reduced terms.
    /// It uses the same panel in a warning color, so it is not mistaken for a
    /// failure.
    /// </summary>
    private void ShowNotice(string message, string? title = null)
    {
        ErrorPanel.Severity = InfoBarSeverity.Warning;
        ErrorPanel.Title = title ?? Strings.OpenTitle;
        ErrorPanel.Message = message;
        SetErrorBarOpen(true);
    }

    // A closed InfoBar still reports its margin as desired size, which leaves
    // an empty strip between the command bar and the canvas and shrinks the
    // area the fit scale is computed against. Collapsing the control in the
    // closed state keeps that strip out of the layout.
    private void SetErrorBarOpen(bool open)
    {
        ErrorPanel.IsOpen = open;
        ErrorPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}
