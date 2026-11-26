// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NopeView.App;

// Window localization: caption strings, error bar chrome, and tree helpers.
public sealed partial class MainWindow : Window
{
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

    /// <summary>
    /// Applies the current language to every string this window owns. The
    /// window carries no hard coded text, so this runs on first load and again
    /// after a switch, without rebuilding the window.
    /// </summary>
    private void ApplyLocalization()
    {
        // A failure message in the previous language would be stale, so the
        // bar closes on a switch instead of mixing two languages.
        SetErrorBarOpen(false);
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

        CopyFileMenuItem.Text = Localization.Get("ContextCopyFile.Text");
        CopyImageMenuItem.Text = Localization.Get("ContextCopyImage.Text");
        CopyPathMenuItem.Text = Localization.Get("ContextCopyPath.Text");
        SaveAsMenuItem.Text = Localization.Get("ContextSaveAs.Text");
        ShowInExplorerMenuItem.Text = Localization.Get("ContextShowInExplorer.Text");
        FitMenuItem.Text = Localization.Get("ContextFit.Text");
        ActualSizeMenuItem.Text = Localization.Get("ContextActualSize.Text");
        SettingsMenuItem.Text = Localization.Get("ContextSettings.Text");

        AutomationProperties.SetName(
            LoadingRing, Localization.Get("Loading.Name"));

        ConfigureErrorBar();
        // The state cache keeps the commands from rebuilding, which would
        // leave the previous language in place after a language switch.
        _animationControlsState = null;
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
}
