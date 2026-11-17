using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using NopeView.App.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;
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

    // App.xaml's CaptionGlyphBrush is the only source for the active color.
    // Only the inactive greys are created here, and they are cached.
    private static readonly SolidColorBrush s_darkInactiveBrush =
        new(ColorHelper.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush s_lightInactiveBrush =
        new(ColorHelper.FromArgb(255, 153, 153, 153));

    private readonly DispatcherTimer _animationTimer = new();

    private bool _titleBarInitialized;
    private bool _windowActive = true;
    private nint _windowHandle;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;

    private IntPtr _routerContext;
    private ImageDirectory? _directory;
    private double _zoom = 1.0;
    private double _rotation;
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    private uint _frameIndex;
    private uint _frameCount = 1;
    private uint _frameDelayMs = 100;
    private bool _isAnimationPlaying;
    private bool _animationLoop = true;
    private bool _animationBusy;

    // Animation timing runs on absolute deadlines. A relative interval per
    // tick accumulates the timer's own latency into every frame, which makes a
    // long animation visibly slower than its declared delays.
    private readonly Stopwatch _animationClock = new();
    private TimeSpan _animationDeadline;

    private bool _isFullScreen;
    private bool _wasMaximizedBeforeFullScreen;

    // The clipboard reads the stream after SetContent returns, so it must
    // outlive the call.
    private InMemoryRandomAccessStream? _clipboardImage;

    // Frames are decoded on a worker thread. A newer request makes an older
    // one stale, so the generation counter discards the superseded result
    // instead of letting it overwrite the newer image.
    private int _decodeGeneration;

    // A single source is reused for every frame. Creating a source per frame
    // allocates a new native image object for each animation step.
    private readonly SoftwareBitmapSource _frameSource = new();

    private readonly Storyboard _loadingAnimation;

    public MainWindow()
    {
        InitializeComponent();
        _loadingAnimation = CreateLoadingAnimation();
        _windowHandle = Handle;
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(100);
        AddOemKeyAccelerators();

        if (!RouterApi.TryCreate(null, out _routerContext))
        {
            _routerContext = IntPtr.Zero;
        }

        // Set the window icon explicitly. For an unpackaged WinUI 3 app the
        // executable's ApplicationIcon alone does not reach Explorer, so the
        // taskbar button, Alt-Tab and thumbnails show a default icon until the
        // running window is given one through AppWindow.SetIcon.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "nopeview.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // Extend content into the title bar so the custom XAML row can own the
        // drag region. The native strip is collapsed later in RootGrid_Loaded.
        ExtendsContentIntoTitleBar = true;

        ConfigureStartupWindow();

        // The caption glyphs turn grey when the window loses focus, exactly as
        // the native title bar buttons do. Both activation and theme changes
        // re-evaluate the glyph color.
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionAppearance();
    }

    /// <summary>
    /// Adds the accelerators for keys that Windows.System.VirtualKey does not
    /// name. The main keyboard's '+' and '-' are OEM keys, so they exist only
    /// as raw virtual-key codes and cannot be declared in XAML.
    /// </summary>
    private void AddOemKeyAccelerators()
    {
        AddAccelerator(0xBB, Windows.System.VirtualKeyModifiers.None, ZoomInAccelerator_Invoked);
        AddAccelerator(0xBB, Windows.System.VirtualKeyModifiers.Shift, ZoomInAccelerator_Invoked);
        AddAccelerator(0xBD, Windows.System.VirtualKeyModifiers.None, ZoomOutAccelerator_Invoked);
    }

    private void AddAccelerator(
        int key,
        Windows.System.VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)key,
            Modifiers = modifiers
        };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateCaptionAppearance();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _animationTimer.Stop();
        Win32Window.UnsuppressSystemMenu(_windowHandle);
        RouterApi.Close(_routerContext);
        _routerContext = IntPtr.Zero;
    }

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

        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var activeBrush = (SolidColorBrush)Application.Current.Resources["CaptionGlyphBrush"];
        var brush = _windowActive
            ? activeBrush
            : (dark ? s_darkInactiveBrush : s_lightInactiveBrush);

        MinimizeButton.Foreground = brush;
        RestoreButton.Foreground = brush;
        CloseButton.Foreground = brush;
        TitleText.Foreground = brush;
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
            // Collapse the system title bar entirely. This removes the native
            // caption buttons and their tooltips; the XAML buttons below replace
            // them and reproduce their geometry and colors.
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }

        // Register the custom row as the drag region now that the system strip
        // is gone. AppTitleBar owns the whole caption area.
        SetTitleBar(AppTitleBar);

        // Keep the maximize/restore glyph in sync with the window state, both
        // on button clicks and on state changes from elsewhere (double click,
        // snap layouts, keyboard).
        UpdateCaptionIcons();
        AppWindow.Changed += AppWindow_Changed;

        // The native system menu is unwanted on the custom title bar. Filter the
        // messages that open it instead of clearing WS_SYSMENU, which would also
        // shift the caption buttons off the visible right edge.
        if (!Win32Window.TrySuppressSystemMenu(_windowHandle))
        {
            System.Diagnostics.Debug.WriteLine("Failed to install the system menu filter.");
        }

        UpdateCaptionAppearance();
        OpenImageFromCommandLine();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Presenter state (maximized/restored/minimized) changes are reported
        // here, including ones not caused by our own caption buttons. The check
        // is cheap, so refresh on every change rather than inspecting args.
        UpdateCaptionIcons();
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

    private void OpenImageFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index + 1 < args.Length; index++)
        {
            if (args[index] == "--open")
            {
                _ = OpenImageAsync(args[index + 1]);
                return;
            }
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = "Open image"
        };

        foreach (var extension in SupportedExtensions())
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            picker.FileTypeFilter.Add(normalized);
        }

        var result = await picker.PickSingleFileAsync();
        if (result is null)
        {
            return;
        }
        await OpenImageAsync(result.Path);
    }

    private IReadOnlyList<RouterFormatInfo> SupportedFormats()
    {
        return _routerContext == IntPtr.Zero
            ? Array.Empty<RouterFormatInfo>()
            : RouterApi.ListFormats(_routerContext);
    }

    private IEnumerable<string> SupportedExtensions()
    {
        foreach (var format in SupportedFormats())
        {
            if (!format.CanDecode)
            {
                continue;
            }
            foreach (var extension in format.Extensions.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return extension;
            }
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null)
        {
            await OpenImageAsync(file.Path);
        }
    }

    private async Task OpenImageAsync(string path)
    {
        StopAnimation();
        _frameCount = 1;
        _frameIndex = 0;
        UpdateAnimationControls();
        _directory = ImageDirectory.Create(path, SupportedExtensions());
        UpdateNavigationState();
        await ShowCurrentImageAsync();
    }

    private async Task ShowCurrentImageAsync()
    {
        if (_directory is null)
        {
            return;
        }

        StopAnimation();
        await DecodeAndShowAsync(_directory.CurrentPath);
        UpdateNavigationState();
    }

    private async Task DecodeAndShowAsync(string path, uint frameIndex = 0u, bool resetView = true)
    {
        if (_routerContext == IntPtr.Zero)
        {
            ShowError("Codec router is unavailable.");
            return;
        }

        var generation = ++_decodeGeneration;
        if (resetView)
        {
            // Only a full open shows the indicator. Animation frames are short
            // enough that a ring would flicker on every step.
            ErrorPanel.Visibility = Visibility.Collapsed;
            SetLoading(true);
        }

        try
        {
            // Decoding, the pixel copy, and the premultiply step all run on a
            // worker thread. Leaving them on the UI thread froze the window for
            // the whole decode and prevented a loading state from rendering.
            var context = _routerContext;
            var frame = await Task.Run(() => DecodeFrame(context, path, frameIndex));

            if (generation != _decodeGeneration)
            {
                // A newer request already owns the view.
                frame.Bitmap?.Dispose();
                return;
            }

            if (frame.Error is not null)
            {
                ShowError("Failed to open image: " + frame.Error);
                if (!resetView)
                {
                    StopAnimation();
                }
                return;
            }

            if (frame.Bitmap is null)
            {
                ShowError(DescribeStatus(frame.Status));
                if (!resetView)
                {
                    StopAnimation();
                }
                return;
            }

            try
            {
                await _frameSource.SetBitmapAsync(frame.Bitmap);
            }
            finally
            {
                // The source copies the pixels, so the decoded bitmap is no
                // longer needed once the update completes.
                frame.Bitmap.Dispose();
            }

            if (generation != _decodeGeneration)
            {
                return;
            }

            if (!ReferenceEquals(ImageView.Source, _frameSource))
            {
                ImageView.Source = _frameSource;
            }

            _imagePixelWidth = frame.PixelWidth;
            _imagePixelHeight = frame.PixelHeight;
            _frameIndex = frameIndex;
            _frameCount = Math.Max(1u, frame.FrameCount);
            _frameDelayMs = frame.FrameDelayMs;
            SetViewControlsEnabled(true);
            UpdateFileStatus(path);
            UpdateViewStatus();
            UpdateAnimationControls();
            UpdateImageAutomationName(path);

            if (resetView)
            {
                ResetView();
                if (_frameCount > 1)
                {
                    _isAnimationPlaying = true;
                    UpdateAnimationControls();
                    ScheduleNextFrame();
                }
                else
                {
                    StopAnimation();
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("Failed to open image: " + ex.Message);
            if (!resetView)
            {
                StopAnimation();
            }
        }
        finally
        {
            // A superseded open must not hide the indicator, because the newer
            // request is still decoding behind it.
            if (resetView && generation == _decodeGeneration)
            {
                SetLoading(false);
            }
        }
    }

    /// <summary>
    /// Decodes one frame entirely on the calling thread. The router image is
    /// released here, so no native pointer ever crosses threads.
    /// </summary>
    private DecodedFrame DecodeFrame(IntPtr context, string path, uint frameIndex)
    {
        var frame = new DecodedFrame();

        try
        {
            if (!RouterApi.TryDecodeFile(
                    context, path, out var image, out var status,
                    frameIndex, RouterApi.PixelFormatBgra8))
            {
                frame.Status = status;
                return frame;
            }

            try
            {
                frame.Bitmap = ToSoftwareBitmap(ref image);
                frame.PixelWidth = image.Width;
                frame.PixelHeight = image.Height;
                frame.FrameCount = image.FrameCount;
                frame.FrameDelayMs = image.FrameDelayMs;
            }
            finally
            {
                RouterApi.FreeImage(ref image);
            }
        }
        catch (Exception ex)
        {
            frame.Error = ex.Message;
        }

        return frame;
    }

    private sealed class DecodedFrame
    {
        internal SoftwareBitmap? Bitmap;
        internal int Status;
        internal int PixelWidth;
        internal int PixelHeight;
        internal uint FrameCount;
        internal uint FrameDelayMs;
        internal string? Error;
    }

    private void SetLoading(bool loading)
    {
        var visible = LoadingRing.Visibility == Visibility.Visible;
        if (loading == visible)
        {
            return;
        }

        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        // The rotation is a dependent animation, so it only needs to run while
        // the indicator is on screen.
        if (loading)
        {
            _loadingAnimation.Begin();
        }
        else
        {
            _loadingAnimation.Stop();
        }
    }

    /// <summary>
    /// Spins the dashed loading ring. A classic storyboard is used because a
    /// ProgressRing's Lottie visual crashes the current WinUI build.
    /// </summary>
    private Storyboard CreateLoadingAnimation()
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1.2)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, LoadingSpinnerRotation);
        Storyboard.SetTargetProperty(spin, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(spin);
        return storyboard;
    }

    private void UpdateImageAutomationName(string path)
    {
        var name = "Current image: " + Path.GetFileName(path);
        if (_frameCount > 1)
        {
            name += $" [frame {_frameIndex + 1}/{_frameCount}]";
        }
        AutomationProperties.SetName(ImageView, name);
    }

    private async void AnimationTimer_Tick(object? sender, object e)
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

        var delay = TimeSpan.FromMilliseconds(
            Math.Clamp((double)_frameDelayMs, 20.0, 10000.0));
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

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        _isAnimationPlaying = false;
        _animationBusy = false;
        UpdateAnimationControls();
    }

    private void UpdateAnimationControls()
    {
        var animated = _frameCount > 1;
        AnimationControls.Visibility = animated ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.IsEnabled = animated;
        LoopButton.IsEnabled = animated;
        PlayPauseLabel.Text = _isAnimationPlaying ? "Pause" : "Play";
        LoopLabel.Text = _animationLoop ? "Loop on" : "Loop off";
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

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = _directory?.HasPrevious == true;
        NextButton.IsEnabled = _directory?.HasNext == true;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await MovePreviousAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await MoveNextAsync();
    }

    private async void PreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MovePreviousAsync();
    }

    private async void NextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MoveNextAsync();
    }

    private void ZoomInAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_zoom * ZoomStep);
    }

    private void ZoomOutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        SetZoom(_zoom / ZoomStep);
    }

    private void ActualSizeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ApplyActualSize();
    }

    private void FitAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        ResetView();
    }

    private void RotateAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }
        args.Handled = true;
        RotateClockwise();
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

    private void EscapeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
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

    private async void CopyImageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CopyCurrentImageAsync();
    }

    private void CopyPathAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CopyCurrentPath();
    }

    private async void SaveAsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveAsAsync();
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

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
        };
    }

    private void PanThumb_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(PanThumb).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        SetZoom(delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
        e.Handled = true;
    }

    private void PanThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_imagePixelWidth == 0)
        {
            return;
        }

        ImageTransform.TranslateX += e.HorizontalChange;
        ImageTransform.TranslateY += e.VerticalChange;
        UpdateViewStatus();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom / ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom * ZoomStep);
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        RotateClockwise();
    }

    private void RotateClockwise()
    {
        _rotation = (_rotation + 90) % 360;
        ImageTransform.Rotation = _rotation;
        UpdateViewStatus();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyActualSize();
    }

    private void ApplyActualSize()
    {
        var ratio = FitToPixelRatio;
        if (ratio <= 0)
        {
            return;
        }

        // 1:1 must be exact even when the needed scale falls outside the
        // interactive wheel limits; those limits only bound wheel zooming.
        _zoom = 1.0 / ratio;
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
    }

    private double FitToPixelRatio
    {
        get
        {
            if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0 ||
                ImageView.ActualWidth <= 0 || ImageView.ActualHeight <= 0)
            {
                return 1.0;
            }

            // Stretch=Uniform fits the bitmap on its limiting axis. Using the
            // control width alone overestimates the fit scale for tall images,
            // which made 1:1 and the zoom percentage wrong.
            var fitScale = Math.Min(
                ImageView.ActualWidth / _imagePixelWidth,
                ImageView.ActualHeight / _imagePixelHeight);
            var rasterizationScale = Viewport.XamlRoot?.RasterizationScale ?? 1.0;
            return fitScale * rasterizationScale;
        }
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ImageTransform.ScaleX = _zoom;
        ImageTransform.ScaleY = _zoom;
        UpdateViewStatus();
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _rotation = 0;
        ImageTransform.ScaleX = 1.0;
        ImageTransform.ScaleY = 1.0;
        ImageTransform.Rotation = 0;
        ImageTransform.TranslateX = 0;
        ImageTransform.TranslateY = 0;
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        var rotation = Math.Round(_rotation);
        var panX = Math.Round(ImageTransform.TranslateX);
        var panY = Math.Round(ImageTransform.TranslateY);
        var pan = Math.Abs(panX) >= 1 || Math.Abs(panY) >= 1
            ? $" | pan {panX:0},{panY:0}"
            : "";
        var frame = _frameCount > 1
            ? $" | {_frameIndex + 1}/{_frameCount}"
            : "";

        if (Math.Abs(_zoom - 1.0) < 0.001)
        {
            ViewStatusText.Text = $"Fit | {rotation:0} deg{frame}{pan}";
            return;
        }

        var percent = _zoom * FitToPixelRatio * 100.0;
        ViewStatusText.Text = $"{Math.Round(percent):0}% | {rotation:0} deg{frame}{pan}";
    }

    private void UpdateFileStatus(string path)
    {
        var name = Path.GetFileName(path);
        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var dimensions = $"{_imagePixelWidth} x {_imagePixelHeight}";
        FileStatusText.Text = string.IsNullOrEmpty(format)
            ? $"{name} | {dimensions}"
            : $"{name} | {dimensions} | {format}";
    }

    private string? CurrentPath => _directory?.CurrentPath;

    private void SetViewControlsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RotateButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
        CopyImageMenuItem.IsEnabled = enabled;
        CopyPathMenuItem.IsEnabled = enabled;
        SaveAsMenuItem.IsEnabled = enabled;
        ShowInExplorerMenuItem.IsEnabled = enabled;
        FitMenuItem.IsEnabled = enabled;
        ActualSizeMenuItem.IsEnabled = enabled;
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(ActionsButton);
        }
    }

    private void FitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ActualSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyActualSize();
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentPath();
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        var data = new DataPackage();
        data.SetText(path);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private async void CopyImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CopyCurrentImageAsync();
    }

    private async Task CopyCurrentImageAsync()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            using var bitmap = await DecodeStraightBitmapAsync(path, _frameIndex);
            if (bitmap is null)
            {
                ShowError(
                    "The current image could not be decoded for copying.",
                    "Could not copy image");
                return;
            }

            // The clipboard accepts a bitmap stream, so the decoded frame is
            // encoded to PNG in memory. Encoding keeps the alpha channel and
            // avoids depending on the source format.
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            stream.Seek(0);

            var data = new DataPackage();
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(data);
            Clipboard.Flush();

            _clipboardImage = stream;
        }
        catch (Exception ex)
        {
            ShowError("Failed to copy image: " + ex.Message, "Could not copy image");
        }
    }

    /// <summary>
    /// Decodes the current frame again as straight (non premultiplied) BGRA,
    /// which is what an encoder and the clipboard expect.
    /// </summary>
    private async Task<SoftwareBitmap?> DecodeStraightBitmapAsync(string path, uint frameIndex)
    {
        var context = _routerContext;
        return await Task.Run(() =>
        {
            if (!RouterApi.TryDecodeFile(
                    context, path, out var image, out _, frameIndex,
                    RouterApi.PixelFormatBgra8))
            {
                return null;
            }

            try
            {
                return CreateStraightBitmap(
                    image.Pixels, image.Width, image.Height, image.Stride);
            }
            finally
            {
                RouterApi.FreeImage(ref image);
            }
        });
    }

    /// <summary>
    /// Copies straight-alpha BGRA pixels into a bitmap. Used by the clipboard,
    /// which must not receive premultiplied colors.
    /// </summary>
    private static unsafe SoftwareBitmap CreateStraightBitmap(
        IntPtr pixels,
        int width,
        int height,
        int stride)
    {
        var rowBytes = width * 4;
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Straight);

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        var description = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        if (reference.As<IMemoryBufferByteAccess>().GetBuffer(out var destination, out _) < 0)
        {
            throw new InvalidOperationException("The image buffer could not be accessed.");
        }

        for (var y = 0; y < height; y++)
        {
            System.Buffer.MemoryCopy(
                (byte*)pixels + (long)y * stride,
                (byte*)destination + description.StartIndex + (long)y * description.Stride,
                rowBytes,
                rowBytes);
        }

        return bitmap;
    }

    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private async Task SaveAsAsync()
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(path),
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = "Save"
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("BMP image", new List<string> { ".bmp" });

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var context = _routerContext;
            var frameIndex = _frameIndex;
            var target = result.Path;

            // The frame is decoded again in its original colors. Reusing the
            // displayed bitmap would save premultiplied pixels.
            await Task.Run(() => SaveFrame(context, path, frameIndex, target));
        }
        catch (Exception ex)
        {
            ShowError("Failed to save image: " + ex.Message, "Could not save image");
        }
    }

    private static void SaveFrame(
        IntPtr context,
        string sourcePath,
        uint frameIndex,
        string targetPath)
    {
        if (!RouterApi.TryDecodeFile(
                context, sourcePath, out var image, out var status, frameIndex,
                RouterApi.PixelFormatRgba8))
        {
            throw new InvalidOperationException(DescribeStatus(status));
        }

        try
        {
            var encodeStatus = RouterApi.EncodeToFile(context, targetPath, ref image);
            if (encodeStatus != 0)
            {
                throw new InvalidOperationException(DescribeEncodeStatus(encodeStatus));
            }
        }
        finally
        {
            RouterApi.FreeImage(ref image);
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var path = CurrentPath;
        if (path is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        });
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
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _isFullScreen = false;
            if (_wasMaximizedBeforeFullScreen &&
                AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        UpdateCaptionIcons();
    }

    /// <summary>
    /// Copies a decoded frame into a SoftwareBitmap by writing the bitmap's
    /// own memory. The earlier path went through a scratch array, DataWriter
    /// and CreateCopyFromBuffer, which copied every pixel three times.
    /// </summary>
    private unsafe SoftwareBitmap ToSoftwareBitmap(ref NvRouterImage image)
    {
        var pixelCount = checked(image.Width * image.Height * 4);
        var rowBytes = image.Width * 4;
        var swap = image.PixelFormat != RouterApi.PixelFormatBgra8;
        var opaque = IsOpaque((byte*)image.Pixels, pixelCount);

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        var description = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        if (reference.As<IMemoryBufferByteAccess>().GetBuffer(out var destination, out _) < 0)
        {
            throw new InvalidOperationException("The image buffer could not be accessed.");
        }

        for (var y = 0; y < image.Height; y++)
        {
            var source = (byte*)image.Pixels + (long)y * image.Stride;
            var target = (byte*)destination
                + description.StartIndex
                + (long)y * description.Stride;

            if (!swap && opaque)
            {
                // Already BGRA with no transparency: no per-pixel work needed.
                System.Buffer.MemoryCopy(source, target, rowBytes, rowBytes);
                continue;
            }

            for (var x = 0; x < rowBytes; x += 4)
            {
                var blue = source[x];
                var green = source[x + 1];
                var red = source[x + 2];
                var alpha = source[x + 3];

                if (swap)
                {
                    (red, blue) = (blue, red);
                }

                if (!opaque)
                {
                    // SoftwareBitmap requires premultiplied alpha.
                    blue = (byte)(blue * alpha / 255);
                    green = (byte)(green * alpha / 255);
                    red = (byte)(red * alpha / 255);
                }

                target[x] = blue;
                target[x + 1] = green;
                target[x + 2] = red;
                target[x + 3] = alpha;
            }
        }

        return bitmap;
    }

    /// <summary>
    /// True when every pixel is fully opaque. Premultiplication is a no-op in
    /// that case, which removes a multiply per channel from the common path.
    /// </summary>
    private static unsafe bool IsOpaque(byte* pixels, int byteCount)
    {
        for (var index = 3; index < byteCount; index += 4)
        {
            if (pixels[index] != 255)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Gives direct access to the bytes of an IMemoryBufferReference. WinRT
    /// exposes the pointer through this non-WinRT interface, which lets pixels
    /// be written into a bitmap without an intermediate buffer.
    /// </summary>
    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        [PreserveSig]
        int GetBuffer(out IntPtr buffer, out uint capacity);
    }

    private void ShowError(string message, string title = "Could not open image")
    {
        ErrorTitle.Text = title;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string DescribeEncodeStatus(int status)
    {
        return status switch
        {
            RouterApi.ErrUnsupportedFormat => "The target format cannot be encoded.",
            RouterApi.ErrEncodeFailed => "The image could not be encoded.",
            RouterApi.ErrOutOfMemory => "Not enough memory to encode this image.",
            RouterApi.ErrIo => "The file could not be written.",
            _ => $"The image could not be saved (status {status})."
        };
    }

    private static string DescribeStatus(int status)
    {
        return status switch
        {
            RouterApi.ErrUnsupportedFormat => "Unsupported image format.",
            RouterApi.ErrDecodeFailed => "The image is damaged or cannot be decoded.",
            RouterApi.ErrOutOfMemory => "Not enough memory to decode this image.",
            RouterApi.ErrIo => "The file could not be read.",
            RouterApi.ErrAbiMismatch => "Codec ABI mismatch.",
            _ => $"Failed to open image (status {status})."
        };
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
