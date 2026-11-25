using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NopeView.App;

// Animation path: frame timing, playback commands, and the frame readout.
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Refreshes the frame readout and the automation name on a slow cadence.
    /// Animation frames switch much faster than a caption can usefully update,
    /// and every text change reaches the title bar through SizeChanged.
    /// </summary>
    private void UpdateAnimationFrameUi(string path)
    {
        var now = Environment.TickCount64;
        if (now - _lastFrameUiUpdate < AnimationUiUpdateIntervalMs)
        {
            return;
        }

        _lastFrameUiUpdate = now;
        UpdateViewStatus();
        UpdateImageAutomationName(path);
    }

    private void UpdateImageAutomationName(string path)
    {
        var name = Localization.Format("Image.Name", Path.GetFileName(path));
        if (_previewActive)
        {
            // Tells assistive tech that the displayed bitmap is downscaled.
            name += Localization.Get("Image.Preview");
        }
        if (_frameCount > 1)
        {
            name += Localization.Format(
                "Image.Frame", _frameIndex + 1, _frameCount);
        }
        AutomationProperties.SetName(ImageView, name);
    }

    private void AnimationTimer_Tick(object? sender, object e)
    {
        _ = RunCommandAsync(AdvanceAnimationAsync, Strings.AnimationTitle);
    }

    private async Task AdvanceAnimationAsync()
    {
        _animationTimer.Stop();
        if (!_isAnimationPlaying || _animationBusy || _directory is null)
        {
            return;
        }

        if (_frameCount <= 1)
        {
            StopAnimation();
            return;
        }

        var next = _frameIndex + 1;
        if (next >= _frameCount)
        {
            if (!_animationLoop)
            {
                StopAnimation();
                return;
            }
            next = 0;
        }

        var path = _directory.CurrentPath;
        _animationBusy = true;
        try
        {
            await DecodeAndShowAsync(path, next, resetView: false);
        }
        finally
        {
            _animationBusy = false;
        }

        if (_isAnimationPlaying)
        {
            ScheduleNextFrame();
        }
    }

    private void ScheduleNextFrame()
    {
        if (!_isAnimationPlaying || _frameCount <= 1)
        {
            return;
        }

        if (!_animationClock.IsRunning)
        {
            _animationClock.Restart();
            _animationDeadline = TimeSpan.Zero;
        }

        var delay = QuantizeToRefresh(_frameDelayMs);
        var now = _animationClock.Elapsed;

        // Add the delay to the previous deadline rather than to the moment the
        // decode finished, so timer latency stays out of the frame duration.
        _animationDeadline += delay;
        if (_animationDeadline < now)
        {
            // A slow decode put us behind schedule. Resume from now instead of
            // firing a burst of catch-up frames.
            _animationDeadline = now;
        }

        _animationTimer.Interval = _animationDeadline - now;
        _animationTimer.Start();
    }

    /// <summary>
    /// Rounds a declared frame delay to a whole number of refresh periods, so
    /// the step lands on a presented frame instead of sliding to the next one.
    /// A delay shorter than a period keeps a single period: faster than the
    /// display cannot be shown, and a zero interval would spin the timer.
    /// </summary>
    private TimeSpan QuantizeToRefresh(uint delayMs)
    {
        var period = Math.Clamp(_refreshPeriodMs, 2.0, 40.0);
        var requested = Math.Clamp((double)delayMs, 5.0, 10000.0);
        var periods = Math.Max(1.0, Math.Round(requested / period));
        return TimeSpan.FromMilliseconds(periods * period);
    }

    // Every presented frame reports here. The interval between them is the
    // display's refresh period, which is measured rather than assumed to be
    // 60 Hz: a delay that falls between two ticks makes the previous frame stay
    // one tick longer, and that reads as a stutter.
    private void CompositionTarget_Rendering(object? sender, object e)
    {
        var now = _renderingClock.Elapsed.TotalMilliseconds;
        var delta = now - _lastRenderingMs;
        _lastRenderingMs = now;

        // Idle gaps between unrelated renders and stalls are not refresh ticks.
        if (delta < 2.0 || delta > 40.0)
        {
            return;
        }

        _refreshPeriodMs = _refreshPeriodMs * 0.7 + delta * 0.3;
    }

    private void StopAnimation()
    {
        var wasPlaying = _isAnimationPlaying;

        _animationTimer.Stop();
        _animationClock.Reset();
        _isAnimationPlaying = false;
        _animationBusy = false;
        UpdateAnimationControls();

        // The throttled frame readout can be behind while playback runs. Pause
        // and stop are the points where the final frame has to be exact.
        if (wasPlaying && _imagePixelWidth > 0 && _directory is not null)
        {
            UpdateViewStatus();
            UpdateImageAutomationName(_directory.CurrentPath);
            _lastFrameUiUpdate = Environment.TickCount64;
        }
    }

    private void UpdateAnimationControls()
    {
        var animated = _frameCount > 1;
        var state = (Animated: animated, Playing: _isAnimationPlaying, Loop: _animationLoop);
        if (_animationControlsState == state)
        {
            return;
        }

        _animationControlsState = state;
        var visibility = animated ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.Visibility = visibility;
        LoopButton.Visibility = visibility;
        PlayPauseButton.IsEnabled = animated;
        LoopButton.IsEnabled = animated;
        PlayPauseButton.IsChecked = _isAnimationPlaying;
        LoopButton.IsChecked = _animationLoop;
        // The command swaps between the play and pause drawings; both are
        // Fluent UI System Icons created fresh by the Icons factory.
        PlayPauseButton.Icon = Icons.CreatePlayPauseIcon(_isAnimationPlaying);
        var playLabel = Localization.Get(
            _isAnimationPlaying ? "Action.Pause" : "Action.Play");
        var loopLabel = Localization.Get(
            _animationLoop ? "Action.LoopOn" : "Action.LoopOff");
        AutomationProperties.SetName(PlayPauseButton, playLabel);
        AutomationProperties.SetName(LoopButton, loopLabel);
        PlayPauseButton.Label = playLabel;
        LoopButton.Label = loopLabel;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_frameCount <= 1)
        {
            return;
        }

        if (_isAnimationPlaying)
        {
            StopAnimation();
            return;
        }

        if (_frameIndex + 1 >= _frameCount)
        {
            _frameIndex = 0;
        }
        _isAnimationPlaying = true;
        UpdateAnimationControls();
        ScheduleNextFrame();
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _animationLoop = !_animationLoop;
        UpdateAnimationControls();
    }

    private void PlayPauseAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_frameCount <= 1)
        {
            return;
        }
        args.Handled = true;
        TogglePlayPause();
    }
}
