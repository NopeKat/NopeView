// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace NopeView.App;

// Navigation: previous/next commands and the direction buttons.
public sealed partial class MainWindow : Window
{
    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = _directory?.HasPrevious == true;
        NextButton.IsEnabled = _directory?.HasNext == true;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MovePreviousAsync, Strings.OpenTitle);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(MoveNextAsync, Strings.OpenTitle);
    }

    private async void PreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MovePreviousAsync, Strings.OpenTitle);
    }

    private async void NextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunCommandAsync(MoveNextAsync, Strings.OpenTitle);
    }

    private async Task MovePreviousAsync()
    {
        if (_directory is not null && _directory.MovePrevious())
        {
            await ShowCurrentImageAsync();
        }
    }

    private async Task MoveNextAsync()
    {
        if (_directory is not null && _directory.MoveNext())
        {
            await ShowCurrentImageAsync();
        }
    }
}
