using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NopeView.App.Native;
using Windows.Foundation;

namespace NopeView.App;

// Custom caption: glyph colors, icon state, and pressed feedback.
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Applies the active/inactive glyph color to the self-drawn caption
    /// buttons and the title text. Native values: white/black while active,
    /// #808080 (dark) or #999999 (light) while inactive. Hover and pressed
    /// states override this through the button templates.
    /// </summary>
    private void UpdateCaptionAppearance()
    {
        if (!_titleBarInitialized)
        {
            return;
        }

        var theme = RootGrid.ActualTheme;
        if (_windowActive)
        {
            // Clearing the local value restores the style setter, so the glyph
            // resolves against this element's theme. Reading a brush from
            // Application.Current.Resources would resolve against the system
            // theme instead, which painted white glyphs on a light title bar.
            MinimizeButton.ClearValue(Control.ForegroundProperty);
            RestoreButton.ClearValue(Control.ForegroundProperty);
            CloseButton.ClearValue(Control.ForegroundProperty);
            TitleText.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        // High contrast has no key of its own yet, so a missing brush leaves
        // the elements on their theme default rather than throwing.
        var inactiveBrush = GetThemeBrush("CaptionGlyphInactiveBrush", theme);
        if (inactiveBrush is null)
        {
            return;
        }

        MinimizeButton.Foreground = inactiveBrush;
        RestoreButton.Foreground = inactiveBrush;
        CloseButton.Foreground = inactiveBrush;
        TitleText.Foreground = inactiveBrush;
    }

    /// <summary>
    /// Looks a brush up in App.xaml's theme dictionary for a specific element
    /// theme, bypassing the application-level theme that a direct
    /// Application.Current.Resources lookup would use.
    /// </summary>
    private static Brush? GetThemeBrush(string key, ElementTheme theme)
    {
        var dictionaryKey = theme == ElementTheme.Dark ? "Dark" : "Light";
        var dictionaries = Application.Current.Resources.ThemeDictionaries;
        if (!dictionaries.TryGetValue(dictionaryKey, out var entry) ||
            entry is not ResourceDictionary dictionary ||
            !dictionary.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as Brush;
    }

    /// <summary>
    /// Shows the restore glyph while maximized and the maximize glyph while
    /// restored, matching the native caption button behavior.
    /// </summary>
    private void UpdateCaptionIcons()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;

        // A maximized window sits at an odd screen offset, which shifts the
        // half-pixel glyph nudge used to align with the native glyphs. Keep the
        // nudge only in the restored state so both states stay pixel-aligned.
        // The minimize glyph sits one pixel lower than the other two, so it
        // needs an extra half-pixel nudge.
        var offset = maximized ? 0.0 : -0.5;
        MinimizeOffset.Y = offset - 0.5;
        CloseOffset.Y = offset;
    }

    /// <summary>
    /// Shows the pressed visual state on the caption button the user is
    /// holding, called from the native filter when a non-client press lands
    /// in the button. The state is reverted when the press ends. The work is
    /// queued instead of running inline: the filter callback happens inside
    /// the window message procedure, and re-entering XAML synchronously
    /// there breaks the move loop that follows the press.
    /// </summary>
    private void OnCaptionPressFeedback(int screenX, int screenY)
    {
        RootGrid.DispatcherQueue.TryEnqueue(() => ApplyCaptionPressedState(screenX, screenY));
    }

    private void OnCaptionReleaseFeedback()
    {
        RootGrid.DispatcherQueue.TryEnqueue(ClearCaptionPressedState);
    }

    private void ApplyCaptionPressedState(int screenX, int screenY)
    {
        var scale = Win32Window.GetScaleFactor(_windowHandle);
        if (scale <= 0)
        {
            return;
        }

        // The screen coordinates and the window origin are physical pixels.
        var x = (screenX - AppWindow.Position.X) / scale;
        var y = (screenY - AppWindow.Position.Y) / scale;
        var button = FindCaptionButtonAt(x, y);
        if (button is null)
        {
            return;
        }

        _movePressedButton = button;
        VisualStateManager.GoToState(button, "Pressed", false);
    }

    private void ClearCaptionPressedState()
    {
        if (_movePressedButton is null)
        {
            return;
        }

        VisualStateManager.GoToState(_movePressedButton, "Normal", false);
        _movePressedButton = null;
    }

    /// <summary>
    /// Returns the visible caption button whose bounds contain the given
    /// window coordinate in XAML units, or null when the point is outside
    /// every button (a plain window move or resize press).
    /// </summary>
    private Button? FindCaptionButtonAt(double x, double y)
    {
        foreach (var button in new[] { MinimizeButton, RestoreButton, CloseButton })
        {
            if (button.Visibility != Visibility.Visible ||
                button.ActualWidth <= 0 ||
                button.ActualHeight <= 0)
            {
                continue;
            }

            var bounds = button.TransformToVisual(null).TransformBounds(
                new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            if (x >= bounds.X && x <= bounds.X + bounds.Width &&
                y >= bounds.Y && y <= bounds.Y + bounds.Height)
            {
                return button;
            }
        }

        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
            UpdateCaptionIcons();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
