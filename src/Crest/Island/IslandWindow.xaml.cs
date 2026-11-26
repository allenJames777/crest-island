using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Crest.Media;
using Crest.Settings;
using Crest.Timers;
using static Crest.Platform.NativeMethods;

namespace Crest.Island;

/// <summary>
/// The always-on-top, see-through window that holds the island. It never takes focus,
/// and only the island itself is drawn, so clicks elsewhere pass through to the apps below.
/// It renders the state it is given and animates between states like the design page.
/// </summary>
public partial class IslandWindow : Window
{
    /// <summary>Base Expanded size in DIP (design page SIZES.expanded) and the room around it.</summary>
    internal const double ExpandedWidth = 392, ExpandedHeight = 206; // 206 since the volume row (was 180)
    internal const double SideMargin = 16, TopRoom = 10, BottomMargin = 24;

    // Motion from the design page (CSS cubic-bezier values).
    private static readonly CubicBezierEase SizeSpring = new(0.3, 1.42, 0.45, 1);
    private static readonly CubicBezierEase RadiusSpring = new(0.3, 1.3, 0.45, 1);
    private static readonly CubicBezierEase ShiftSpring = new(0.3, 1.25, 0.45, 1);
    private static readonly CubicBezierEase LayerSpring = new(0.3, 1.4, 0.45, 1);
    private static readonly CubicBezierEase Shrink = new(0.4, 0, 0.2, 1);
    private static readonly CubicBezierEase CssEase = new(0.25, 0.1, 0.25, 1);

    public static readonly DependencyProperty IslandWidthProperty = ShapeProperty(nameof(IslandWidth), 236);
    public static readonly DependencyProperty IslandHeightProperty = ShapeProperty(nameof(IslandHeight), 38);
    public static readonly DependencyProperty RadiusTopProperty = ShapeProperty(nameof(RadiusTop), 0);
    public static readonly DependencyProperty RadiusBottomProperty = ShapeProperty(nameof(RadiusBottom), 19);
    public static readonly DependencyProperty IslandShiftProperty = ShapeProperty(nameof(IslandShift), 0);
    public static readonly DependencyProperty ThemeBlendProperty = DependencyProperty.Register(nameof(ThemeBlend),
        typeof(double), typeof(IslandWindow), new PropertyMetadata(1.0, (d, _) => ((IslandWindow)d).ApplyBlend()));

    private static readonly double[] EqRatios = [0.5, 0.9, 0.62, 0.78];
    /// <summary>Seconds for one full bounce of each bar (design page keyframe durations).</summary>
    private static readonly double[] EqSeconds = [0.86, 1.14, 0.95, 1.26];

    private static readonly Geometry PlayGeometry = Freeze(Geometry.Parse(
        "M8,5.6 V18.4 A1,1 0 0 0 9.52,19.25 L19.92,12.85 A1,1 0 0 0 19.92,11.15 L9.52,4.75 A1,1 0 0 0 8,5.6 Z"));
    // Speaker waves on a 24 x 24 grid: none, one, two, or a cross when muted.
    private static readonly Geometry WavesLow = Freeze(Geometry.Parse("M15,9.5 A3.5,3.5 0 0 1 15,14.5"));
    private static readonly Geometry WavesHigh = Freeze(Geometry.Parse("M15,9.5 A3.5,3.5 0 0 1 15,14.5 M17.8,6.8 A7,7 0 0 1 17.8,17.2"));
    private static readonly Geometry WavesMuted = Freeze(Geometry.Parse("M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5"));

    private static readonly Geometry PauseGeometry = Freeze(new GeometryGroup
    {
        Children =
        {
            new RectangleGeometry(new Rect(6.5, 5, 4, 14), 1.3, 1.3),
            new RectangleGeometry(new Rect(13.5, 5, 4, 14), 1.3, 1.3),
        },
    });

    private IntPtr _hwnd;
    private AppSettings _settings = AppSettings.Defaults;
    private NowPlaying _nowPlaying = NowPlaying.Empty;
    private IslandState _state = IslandState.Tucked;
    private IslandPage _page = IslandPage.Music;
    private double _prevArea;
    private bool _animate = true;
    private bool _pointerOver;
    private readonly DispatcherTimer _pointerRecheck;
    private readonly DispatcherTimer _seekTimer;
    private bool _dragging;
    private double _dragFraction;
    private bool _volumeDragging;
    private double _volumeLevel;
    private bool _volumeMuted;
    private BitmapSource? _hueSource;
    private double? _artHue;
    private IslandColors _colors = ThemePalette.For(AppSettings.Defaults, null);
    private IslandColors? _fromColors;
    private IslandColors? _toColors;

    public IslandWindow()
    {
        InitializeComponent();
        foreach (var layer in Layers) layer.RenderTransform = new ScaleTransform(0.94, 0.94);
        BuildEqualizer(CompactEq, 14, 2.5);
        BuildEqualizer(PeekEq, 14, 2.5);
        BuildEqualizer(ExpandedEq, 18, 3);
        PlayIcon.Data = PlayGeometry;
        SourceInitialized += OnSourceInitialized;
        // Clicking away while typing ends it: the island is no-activate again.
        Deactivated += (_, _) => _keyboard.GiveBack(_hwnd);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnDisplay, DispatcherPriority.Background);

        foreach (var target in new UIElement[] { Body, HotStrip })
        {
            target.MouseEnter += (_, _) => QueuePointerCheck();
            target.MouseLeave += (_, _) => QueuePointerCheck();
            target.MouseLeftButtonUp += OnIslandClick;
        }
        // The island can grow or shrink under a cursor that is not moving; check again once the motion settles.
        _pointerRecheck = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(650) };
        _pointerRecheck.Tick += (_, _) => { _pointerRecheck.Stop(); EvaluatePointer(); };

        // The seek bar moves 4 times a second, and only while Expanded and playing.
        _seekTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _seekTimer.Tick += (_, _) => UpdateSeek();
        SeekTrack.MouseEnter += (_, _) => SetSeekHover(true);
        SeekTrack.MouseLeave += (_, _) => SetSeekHover(_dragging);
        SeekTrack.MouseLeftButtonDown += OnSeekDown;
        SeekTrack.MouseMove += OnSeekMove;
        SeekTrack.MouseLeftButtonUp += OnSeekUp;
        SeekTrack.LostMouseCapture += (_, _) => EndSeekDrag(commit: false);
        SeekTrack.SizeChanged += (_, _) => UpdateSeek();
        VolumeTrack.MouseEnter += (_, _) => SetVolumeHover(true);
        VolumeTrack.MouseLeave += (_, _) => SetVolumeHover(_volumeDragging);
        VolumeTrack.MouseLeftButtonDown += OnVolumeDown;
        VolumeTrack.MouseMove += OnVolumeMove;
        VolumeTrack.MouseLeftButtonUp += OnVolumeUp;
        VolumeTrack.LostMouseCapture += (_, _) =>
        {
            if (!_volumeDragging) return;
            _volumeDragging = false;
            SetVolumeHover(VolumeTrack.IsMouseOver);
            QueuePointerCheck();
        };
        VolumeTrack.SizeChanged += (_, _) => RenderVolume();
        Body.MouseWheel += OnWheel;
        HotStrip.MouseWheel += OnWheel;
        Body.MouseRightButtonUp += OnRightClick;
        HotStrip.MouseRightButtonUp += OnRightClick;
        TimerListScroll.PreviewMouseWheel += OnTimerListWheel;
        ExpandedText.SizeChanged += (_, _) => FitChipRow();
        TimerChip.SizeChanged += (_, _) => FitChipRow();

        ApplyTheme(animate: false);
        UpdateShape();
        ShowState(_state, _page);
        ShowNowPlaying(NowPlaying.Empty);
    }

    /// <summary>The pointer moved onto the island (or the Tucked hover strip).</summary>
    internal event Action? PointerEnteredIsland;

    /// <summary>The pointer left the island.</summary>
    internal event Action? PointerLeftIsland;

    /// <summary>A click on the island that did not land on a button or the seek bar.</summary>
    internal event Action? BackgroundClicked;

    internal event Action? PlayPauseRequested;
    internal event Action? NextRequested;
    internal event Action? PreviousRequested;
    internal event Action? ShuffleRequested;
    internal event Action? RepeatRequested;
    internal event Action<TimeSpan>? SeekRequested;

    /// <summary>The app chip was clicked with two or more apps that have media.</summary>
    internal event Action? SwitchAppRequested;

    /// <summary>A right-click on the island: open Crest's menu (the same one as the tray).</summary>
    internal event Action? MenuRequested;

    /// <summary>The volume slider was moved to a level from 0 to 1.</summary>
    internal event Action<double>? VolumeChangeRequested;
    internal event Action? MuteToggleRequested;

    /// <summary>The mouse wheel turned over the island (120 per notch; positive is up).</summary>
    internal event Action<int>? VolumeWheel;

    public double IslandWidth { get => (double)GetValue(IslandWidthProperty); set => SetValue(IslandWidthProperty, value); }
    public double IslandHeight { get => (double)GetValue(IslandHeightProperty); set => SetValue(IslandHeightProperty, value); }
    public double RadiusTop { get => (double)GetValue(RadiusTopProperty); set => SetValue(RadiusTopProperty, value); }
    public double RadiusBottom { get => (double)GetValue(RadiusBottomProperty); set => SetValue(RadiusBottomProperty, value); }
    public double IslandShift { get => (double)GetValue(IslandShiftProperty); set => SetValue(IslandShiftProperty, value); }

    internal IslandState CurrentState => _state;

    private Grid[] Layers => [TuckLayer, TimerPillLayer, CompactLayer, PeekLayer, ExpandedLayer, TimersLayer];

    internal IslandPage CurrentPage => _page;

    /// <summary>The open island on its music page (the seek bar and its equalizer are only seen here).</summary>
    private bool MusicShown => _state == IslandState.Expanded && _page == IslandPage.Music;

    /// <summary>The open island on its Timers page.</summary>
    private bool TimersShown => _state == IslandState.Expanded && _page == IslandPage.Timers;

    // ----- window setup -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PlaceOnDisplay();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            // Clicking the island never takes focus, except while a timer name is being typed.
            handled = true;
            return new IntPtr(Platform.IslandKeyboard.AllowActivation ? MA_ACTIVATE : MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Takes the keyboard while a timer name is typed, and gives it back (decision D-20261125-crest-timers-type-in-island).</summary>
    private readonly Platform.IslandKeyboard _keyboard = new();

    internal void PlaceOnDisplay()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = _settings.Scale / 100.0;
        double w = ExpandedWidth * scale + SideMargin * 2;
        double h = ExpandedHeight * scale + TopRoom + BottomMargin;
        var display = IslandPositioner.Choose(_settings.DisplayDeviceName);
        if (display is null) return;
        IslandPositioner.Place(_hwnd, display, w, h, _settings.TopOffset, _settings.HorizontalPosition);
    }

    /// <summary>Windows' "Animation effects" setting, used when Animations is FollowWindows.</summary>
    internal bool WindowsAnimationsOn { get; set; } = true;

    /// <summary>The island window's handle (zero until it is shown).</summary>
    internal IntPtr Handle => _hwnd;

    /// <summary>Puts the island back above other topmost windows (called when another window comes to the front).</summary>
    /// <summary>Checks again whether the pointer is on the island (for example after Crest's menu closes).</summary>
    internal void RecheckPointer() => QueuePointerCheck();

    /// <summary>While Crest's own menu is open over the island, the pointer counts as still on the island.</summary>
    internal bool MenuOpen { get; set; }

    internal void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero) IslandPositioner.KeepOnTop(_hwnd);
    }

    // ----- settings, theme and content -----

    internal void ApplySettings(AppSettings settings)
    {
        bool placementChanged = settings.Scale != _settings.Scale
            || settings.TopOffset != _settings.TopOffset
            || settings.HorizontalPosition != _settings.HorizontalPosition
            || !string.Equals(settings.DisplayDeviceName, _settings.DisplayDeviceName, StringComparison.OrdinalIgnoreCase);
        _settings = settings;
        _animate = settings.Animations switch
        {
            AnimationMode.On => true,
            AnimationMode.Off => false,
            _ => WindowsAnimationsOn,
        };
        BodyScale.ScaleX = BodyScale.ScaleY = settings.Scale / 100.0;
        if (placementChanged) PlaceOnDisplay();
        ApplyTheme(animate: _animate);
        ShowNowPlaying(_nowPlaying);
        ShowState(_state, _page);
    }

    /// <summary>
    /// Sets every island colour from the theme and the album art's hue, blending from the current colours over 500 ms.
    /// Resource brushes are frozen by WPF once shared, so each step of the blend puts in fresh brushes instead of
    /// animating one brush.
    /// </summary>
    private void ApplyTheme(bool animate)
    {
        var target = ThemePalette.For(_settings, _artHue);
        _fromColors = BlendedColors();
        _toColors = target;
        _colors = target;
        BeginAnimation(ThemeBlendProperty, null);
        if (animate)
        {
            ThemeBlend = 0;
            ApplyBlend();
            var blend = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = CssEase };
            blend.Freeze();
            BeginAnimation(ThemeBlendProperty, blend);
        }
        else
        {
            ThemeBlend = 1;
            ApplyBlend();
        }

        var c = target;

        SeekFill.Background = Freeze(new LinearGradientBrush(ToColor(c.Accent), ToColor(c.Accent2), 0));
        var glow = new RadialGradientBrush
        {
            GradientOrigin = new Point(0, 0),
            Center = new Point(0, 0),
            RadiusX = 1.3,
            RadiusY = 1.0,
            GradientStops =
            {
                new GradientStop(ToColor(c.Accent.WithAlpha(0.34)), 0),
                new GradientStop(ToColor(c.Accent.WithAlpha(0)), 0.62),
            },
        };
        Glow.Fill = Freeze(glow);
        Glow.Opacity = c.Glow;
        TimersGlow.Fill = Glow.Fill;
        TimersGlow.Opacity = c.Glow;
        _accentGradient = Freeze(new LinearGradientBrush(ToColor(c.Accent), ToColor(c.Accent2), 45));
        RenderTimerTile();
        UpdateArt();
        UpdateToggleColours();
    }

    public double ThemeBlend { get => (double)GetValue(ThemeBlendProperty); set => SetValue(ThemeBlendProperty, value); }

    /// <summary>The colours shown right now, part way through a blend or at its end.</summary>
    private IslandColors BlendedColors()
    {
        if (_fromColors is null || _toColors is null) return _colors;
        double t = Math.Clamp(ThemeBlend, 0, 1);
        return _toColors with
        {
            Background = Lerp(_fromColors.Background, _toColors.Background, t),
            Foreground = Lerp(_fromColors.Foreground, _toColors.Foreground, t),
            Muted = Lerp(_fromColors.Muted, _toColors.Muted, t),
            Line = Lerp(_fromColors.Line, _toColors.Line, t),
            Track = Lerp(_fromColors.Track, _toColors.Track, t),
            Hover = Lerp(_fromColors.Hover, _toColors.Hover, t),
            Button = Lerp(_fromColors.Button, _toColors.Button, t),
            ButtonForeground = Lerp(_fromColors.ButtonForeground, _toColors.ButtonForeground, t),
            Accent = Lerp(_fromColors.Accent, _toColors.Accent, t),
        };
    }

    private void ApplyBlend()
    {
        var c = BlendedColors();
        Resources["IslandBg"] = Brush(c.Background);
        Resources["IslandFg"] = Brush(c.Foreground);
        Resources["IslandMuted"] = Brush(c.Muted);
        Resources["IslandLine"] = Brush(c.Line);
        Resources["IslandTrack"] = Brush(c.Track);
        Resources["IslandHover"] = Brush(c.Hover);
        Resources["IslandButton"] = Brush(c.Button);
        Resources["IslandButtonFg"] = Brush(c.ButtonForeground);
        Resources["IslandAccent"] = Brush(c.Accent);

        static Brush Brush(Rgba value) => Freeze(new SolidColorBrush(ToColor(value)));
    }

    private static Rgba Lerp(Rgba a, Rgba b, double t)
    {
        static byte Mix(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return new Rgba(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), Mix(a.A, b.A, t));
    }

    /// <summary>Shows a now-playing snapshot in every layer.</summary>
    internal void ShowNowPlaying(NowPlaying np)
    {
        _nowPlaying = np;
        bool has = np.HasSession;
        string title = !has ? "Nothing playing" : string.IsNullOrWhiteSpace(np.Title) ? np.SourceAppName : np.Title;
        string sub = string.Join(" · ", new[] { np.Artist, np.SourceAppName }.Where(s => !string.IsNullOrWhiteSpace(s)));

        CompactTitle.Text = title;
        PeekTitle.Text = title;
        PeekSub.Text = sub;
        ExpandedTitle.Text = title;
        ExpandedArtist.Text = np.Artist;
        SongChipText.Text = has ? title : "Music";
        bool several = np.SessionCount >= 2;
        ExpandedApp.Text = several ? $"{np.SourceAppName} · {np.SessionIndex} of {np.SessionCount}" : np.SourceAppName;
        // With several apps the chip is the switcher: it stays clickable, and the title keeps to one line so it fits.
        ExpandedAppChip.IsHitTestVisible = several;
        ExpandedAppChip.Cursor = several ? Cursors.Hand : null;
        ExpandedAppChip.ToolTip = several ? "Show the next app" : null;
        ExpandedArtist.Visibility = has && np.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppChip();

        // A new picture changes the accent (and the Album tint background).
        if (!ReferenceEquals(np.Art, _hueSource))
        {
            _hueSource = np.Art;
            double? hue = np.Art is null ? null : HueOf(np.Art);
            if (hue != _artHue)
            {
                _artHue = hue;
                ApplyTheme(_animate);
            }
        }
        UpdateArt();

        var eqVisibility = has && _settings.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = PeekEq.Visibility = ExpandedEq.Visibility = eqVisibility;

        // Controls: disabled with no session; shuffle and repeat only when the app supports them.
        PlayIcon.Data = np.IsPlaying ? PauseGeometry : PlayGeometry;
        PlayButton.IsEnabled = has && np.Can.PlayPause;
        PreviousButton.IsEnabled = has && np.Can.Previous;
        NextButton.IsEnabled = has && np.Can.Next;
        ShuffleButton.Visibility = has && np.Can.Shuffle ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.Visibility = has && np.Can.Repeat ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggleColours();

        // Seek bar: hidden without a timeline; read-only when the app does not allow seeking.
        SeekRow.Visibility = has && np.Timeline is not null ? Visibility.Visible : Visibility.Hidden;
        SeekTrack.Cursor = np.Can.Seek ? Cursors.Hand : Cursors.Arrow;
        UpdateSeek();
        UpdateSeekTimer();
        UpdateEqualizers();
    }

    private static readonly Brush DarkPlaceholder = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)));
    private static readonly Brush LightPlaceholder = Freeze(new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xE0)));
    private BitmapSource? _artBrushSource;
    private Brush? _artBrush;

    /// <summary>Sets the album art on every layer; the image brush is only rebuilt when the picture changes.</summary>
    private void UpdateArt()
    {
        Brush art;
        if (_nowPlaying.Art is null)
        {
            art = _colors.IsLight ? LightPlaceholder : DarkPlaceholder;
        }
        else
        {
            if (!ReferenceEquals(_nowPlaying.Art, _artBrushSource) || _artBrush is null)
            {
                _artBrushSource = _nowPlaying.Art;
                _artBrush = Freeze(new ImageBrush(_nowPlaying.Art) { Stretch = Stretch.UniformToFill });
            }
            art = _artBrush;
        }
        var visibility = _nowPlaying.HasSession && _settings.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
        foreach (var border in new[] { CompactArt, PeekArt, ExpandedArt })
        {
            if (!ReferenceEquals(border.Background, art)) border.Background = art;
            border.Visibility = visibility;
        }
    }

    /// <summary>The app chip shows whenever an app has media. The title keeps to one line, so the chip row always fits.</summary>
    private void UpdateAppChip()
    {
        ExpandedAppChip.Visibility = _nowPlaying.HasSession && _nowPlaying.SourceAppName.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A long app name trims with "…" so the stopwatch chip beside it always stays whole.</summary>
    private void FitChipRow()
    {
        double room = ExpandedText.ActualWidth - TimerChip.ActualWidth - TimerChip.Margin.Left;
        ExpandedAppChip.MaxWidth = Math.Max(0, room);
    }

    // ----- timers (spec 2026-11-25-crest-timers) -----

    private static readonly Geometry PlusGeometry = Freeze(new GeometryGroup
    {
        Children =
        {
            new RectangleGeometry(new Rect(10.9, 5, 2.2, 14), 1.1, 1.1),
            new RectangleGeometry(new Rect(5, 10.9, 14, 2.2), 1.1, 1.1),
        },
    });

    private TimerBook? _timerBook;
    private Brush? _accentGradient;

    /// <summary>The top row's inline confirm: which timer, and whether it asks to reset (else to delete).</summary>
    private (Guid Id, bool IsReset)? _topConfirm;

    /// <summary>A page chip was clicked: the stopwatch chip asks for Timers, the song chip for Music.</summary>
    internal event Action<IslandPage>? PageRequested;

    /// <summary>Start or pause a timer.</summary>
    internal event Action<Guid>? TimerToggleRequested;

    /// <summary>Reset a timer to 0:00 (the user confirmed it).</summary>
    internal event Action<Guid>? TimerResetRequested;

    /// <summary>Delete a timer (the user confirmed it).</summary>
    internal event Action<Guid>? TimerDeleteRequested;

    private void OnTimerChip(object sender, RoutedEventArgs e) => PageRequested?.Invoke(IslandPage.Timers);

    private void OnSongChip(object sender, RoutedEventArgs e) => PageRequested?.Invoke(IslandPage.Music);

    /// <summary>Shows the timers: the pill, the music page's stopwatch chip and the Timers page.</summary>
    internal void ShowTimers(TimerBook book, DateTimeOffset now)
    {
        _timerBook = book;
        // A confirm belongs to the timer where it was asked: drop it once that timer moves or goes.
        if (_topConfirm is { } c && book.Top?.Id != c.Id) _topConfirm = null;
        RenderTimersPage(now);
        RenderTimerList(now);
        UpdateTimerTimes(now, all: true);
    }

    /// <summary>
    /// Updates only the times shown: the pill in whole minutes, the open island in seconds. Only visible text changes
    /// (unless <paramref name="all"/>), because any change redraws the whole see-through window.
    /// </summary>
    internal void UpdateTimerTimes(DateTimeOffset now, bool all = false)
    {
        var running = _timerBook?.Running;
        if (all || _state == IslandState.TimerPill)
            TimerPillText.Text = TimeFormat.Minutes(running?.Elapsed(now) ?? TimeSpan.Zero);
        if (all || MusicShown)
        {
            string clock = running is null ? "" : TimeFormat.Clock(running.Elapsed(now));
            TimerChipText.Text = clock;
            TimerChipText.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
            TimerChip.ToolTip = running is null ? "Timers" : $"{running.Name} · open timers";
            System.Windows.Automation.AutomationProperties.SetName(TimerChip,
                running is null ? "Open timers" : $"Timer {running.Name}, {clock}. Open timers");
        }
        if (all || TimersShown)
            TimerTime.Text = TimeFormat.Clock(_timerBook?.Top?.Elapsed(now) ?? TimeSpan.Zero);
    }

    /// <summary>The Timers page's top row: the running timer, else the last one used, else a "+" to add the first.</summary>
    private void RenderTimersPage(DateTimeOffset now)
    {
        var top = _timerBook?.Top;
        bool running = top?.IsRunning == true;
        RenderTimerTile();
        TimerName.Text = top?.Name ?? "No timers yet";
        TimerTime.SetResourceReference(TextBlock.ForegroundProperty, running ? "IslandAccent" : "IslandMuted");
        TimerTime.Text = TimeFormat.Clock(top?.Elapsed(now) ?? TimeSpan.Zero);

        bool confirming = top is not null && _topConfirm is { } c && c.Id == top.Id;
        TimerActions.Visibility = top is not null && !confirming ? Visibility.Visible : Visibility.Collapsed;
        TimerConfirm.Visibility = confirming ? Visibility.Visible : Visibility.Collapsed;
        if (confirming) TimerConfirmText.Text = _topConfirm!.Value.IsReset ? "Reset?" : "Delete?";
        if (top is not null)
        {
            System.Windows.Automation.AutomationProperties.SetName(TimerResetButton, $"Reset {top.Name}");
            System.Windows.Automation.AutomationProperties.SetName(TimerDeleteButton, $"Delete {top.Name}");
        }
    }

    /// <summary>The tile glows in the accent gradient with a pause icon while running; plain with play when paused.</summary>
    private void RenderTimerTile()
    {
        var top = _timerBook?.Top;
        bool running = top?.IsRunning == true;
        if (running && _accentGradient is not null) TimerTile.Background = _accentGradient;
        else TimerTile.SetResourceReference(BackgroundProperty, "IslandHover");
        TimerTileIcon.Data = top is null ? PlusGeometry : running ? PauseGeometry : PlayGeometry;
        TimerTileIcon.SetResourceReference(Shape.FillProperty, running ? "IslandButtonFg" : "IslandFg");
        System.Windows.Automation.AutomationProperties.SetName(TimerTile,
            top is null ? "New timer" : $"{(running ? "Pause" : "Start")} {top.Name}");
    }

    private void OnTimerTile(object sender, RoutedEventArgs e)
    {
        var top = _timerBook?.Top;
        if (top is null)
        {
            StartTyping(); // no timers yet: the "+" tile adds the first one
            return;
        }
        _topConfirm = null;
        TimerToggleRequested?.Invoke(top.Id);
    }

    private void OnTopReset(object sender, RoutedEventArgs e) => AskTop(isReset: true);

    private void OnTopDelete(object sender, RoutedEventArgs e) => AskTop(isReset: false);

    private void AskTop(bool isReset)
    {
        if (_timerBook?.Top is not { } top) return;
        _topConfirm = (top.Id, isReset);
        RenderTimersPage(DateTimeOffset.Now);
    }

    private void OnTopConfirmYes(object sender, RoutedEventArgs e)
    {
        if (_topConfirm is not { } c) return;
        _topConfirm = null;
        if (c.IsReset) TimerResetRequested?.Invoke(c.Id);
        else TimerDeleteRequested?.Invoke(c.Id);
        RenderTimersPage(DateTimeOffset.Now);
    }

    private void OnTopConfirmNo(object sender, RoutedEventArgs e)
    {
        _topConfirm = null;
        RenderTimersPage(DateTimeOffset.Now);
    }

    // ----- the Timers page's list (plan task 6b) -----

    /// <summary>A list row's inline confirm: which timer, and whether it asks to reset (else to delete).</summary>
    private (Guid Id, bool IsReset)? _rowConfirm;

    /// <summary>
    /// The other timers as rows, in the order they were added, then the New timer row. While a name is being typed,
    /// the text box row stays in place (removing it would take its keyboard focus and end the typing).
    /// </summary>
    private void RenderTimerList(DateTimeOffset now)
    {
        var keep = _typing ? _newTimerRow : null;
        for (int i = TimerList.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(TimerList.Children[i], keep)) TimerList.Children.RemoveAt(i);
        if (_timerBook is null) return;
        if (_rowConfirm is { } c && _timerBook.Others.All(t => t.Id != c.Id)) _rowConfirm = null;
        int index = 0;
        foreach (var timer in _timerBook.Others) TimerList.Children.Insert(index++, BuildTimerRow(timer, now));
        if (keep is null) TimerList.Children.Add(BuildNewTimerRow());
        else if (!TimerList.Children.Contains(keep)) TimerList.Children.Add(keep);
    }

    // ----- typing a new timer's name (plan task 7, decision D-20261125-crest-timers-type-in-island) -----

    private bool _typing;
    private Border? _newTimerRow;
    private TextBox? _timerInput;

    /// <summary>A timer name is being typed: the island must stay open.</summary>
    internal event Action? TypingStarted;

    /// <summary>Typing ended (Enter, Esc, or the keyboard went elsewhere).</summary>
    internal event Action? TypingEnded;

    /// <summary>Add a timer with this name and start it.</summary>
    internal event Action<string>? TimerAddRequested;

    /// <summary>Turns the New timer row into a text box and takes the keyboard. Called from a real click.</summary>
    private void StartTyping()
    {
        if (_typing || _timerBook is null) return;
        _typing = true;
        _topConfirm = _rowConfirm = null;
        _newTimerRow = BuildTimerInputRow();
        DateTimeOffset now = DateTimeOffset.Now;
        RenderTimersPage(now);
        RenderTimerList(now);
        TypingStarted?.Invoke();
        TimerListScroll.ScrollToEnd();
        // If Windows refuses to bring the island to the front, no keyboard will come: stop typing at once.
        if (!_keyboard.Take(_hwnd, _timerInput!, Dispatcher)) FinishTyping(keep: false);
    }

    /// <summary>Ends typing: gives the keyboard back and, on Enter with a name, asks for the timer to be added.</summary>
    private void FinishTyping(bool keep)
    {
        if (!_typing) return;
        _typing = false; // first: giving the keyboard back raises LostKeyboardFocus, which calls this again
        string name = _timerInput?.Text ?? "";
        _keyboard.GiveBack(_hwnd);
        _newTimerRow = null;
        _timerInput = null;
        // Add first, while the island is still held, so the new rest state waits for the usual collapse delay.
        if (keep && TimerBook.CleanName(name) is not null) TimerAddRequested?.Invoke(name);
        else RenderTimerList(DateTimeOffset.Now);
        TypingEnded?.Invoke();
        Dispatcher.BeginInvoke(() => TimerListScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    /// <summary>The typing row (design page .tnew.on): an accent tint, the text box with its placeholder, and an Enter hint.</summary>
    private Border BuildTimerInputRow()
    {
        var row = new Border
        {
            Height = 28, Margin = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 0, 6, 0), Tag = "control",
            Background = Freeze(new SolidColorBrush(ToColor(_colors.Accent.WithAlpha(0.15)))),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12) };
        ring.SetResourceReference(Border.BackgroundProperty, "IslandAccent");
        var plus = new Path { Data = PlusGeometry, Width = 11, Height = 11, Stretch = Stretch.Uniform };
        plus.SetResourceReference(Shape.FillProperty, "IslandButtonFg");
        ring.Child = plus;
        grid.Children.Add(ring);

        var input = new TextBox
        {
            MaxLength = TimerBook.MaxNameLength, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        input.SetResourceReference(ForegroundProperty, "IslandFg");
        input.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "IslandFg");
        Label(input, "New timer name", "NewTimerName");
        var placeholder = new TextBlock
        {
            Text = "What are you timing?", FontSize = 13, Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "IslandMuted");
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; FinishTyping(keep: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; FinishTyping(keep: false); }
        };
        input.LostKeyboardFocus += (_, _) => FinishTyping(keep: false);
        Grid.SetColumn(input, 1);
        Grid.SetColumn(placeholder, 1);
        grid.Children.Add(placeholder);
        grid.Children.Add(input);
        _timerInput = input;

        var hint = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(Border.BorderBrushProperty, "IslandTrack");
        var hintText = new TextBlock { Text = "Enter", FontSize = 10.5 };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "IslandMuted");
        hint.Child = hintText;
        Grid.SetColumn(hint, 2);
        grid.Children.Add(hint);
        return row;
    }

    private FrameworkElement BuildTimerRow(ActivityTimer timer, DateTimeOffset now)
    {
        // Tag "control": a click on the row is not a click on the island's background.
        var row = new Border
        {
            Height = 28, Margin = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 0, 6, 0), Background = Brushes.Transparent, Tag = "control",
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        var playIcon = new Path { Data = PlayGeometry, Width = 12, Height = 12, Stretch = Stretch.Uniform };
        playIcon.SetResourceReference(Shape.FillProperty, "IslandFg");
        var play = new Button { Style = (Style)FindResource("RowPlayButton"), Content = playIcon };
        Label(play, $"Start {timer.Name}", "RowStart");
        play.Click += (_, _) => { _rowConfirm = null; TimerToggleRequested?.Invoke(timer.Id); };
        grid.Children.Add(play);

        var name = new TextBlock
        {
            Text = timer.Name, FontSize = 13, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("Sub"),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var tail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tail, 2);
        grid.Children.Add(tail);

        if (_rowConfirm is { } c && c.Id == timer.Id)
        {
            var ask = new TextBlock { Text = c.IsReset ? "Reset to 0:00?" : "Delete timer?", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            ask.SetResourceReference(TextBlock.ForegroundProperty, "IslandFg");
            tail.Children.Add(ask);
            var yes = SmallButton(CheckGeometry, "Yes", "RowYes");
            yes.SetResourceReference(ForegroundProperty, "IslandAccent");
            yes.Click += (_, _) =>
            {
                _rowConfirm = null;
                if (c.IsReset) TimerResetRequested?.Invoke(timer.Id);
                else TimerDeleteRequested?.Invoke(timer.Id);
                RenderTimerList(DateTimeOffset.Now);
            };
            var no = SmallButton(CrossGeometry, "No", "RowNo");
            no.Margin = new Thickness(2, 0, 0, 0);
            no.Click += (_, _) => { _rowConfirm = null; RenderTimerList(DateTimeOffset.Now); };
            tail.Children.Add(yes);
            tail.Children.Add(no);
            row.SetResourceReference(Border.BackgroundProperty, "IslandHover");
            return row;
        }

        // Reset and delete show while the pointer is on the row (design page .ta).
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
        var reset = SmallButton(ResetGeometry, $"Reset {timer.Name}", "RowReset");
        reset.Click += (_, _) => { _rowConfirm = (timer.Id, true); RenderTimerList(DateTimeOffset.Now); };
        var delete = SmallButton(CrossGeometry, $"Delete {timer.Name}", "RowDelete");
        delete.Margin = new Thickness(2, 0, 0, 0);
        delete.Click += (_, _) => { _rowConfirm = (timer.Id, false); RenderTimerList(DateTimeOffset.Now); };
        actions.Children.Add(reset);
        actions.Children.Add(delete);
        tail.Children.Add(actions);

        var time = new TextBlock
        {
            Text = TimeFormat.Clock(timer.Elapsed(now)), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "IslandMuted");
        System.Windows.Documents.Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        tail.Children.Add(time);

        row.MouseEnter += (_, _) => { row.SetResourceReference(Border.BackgroundProperty, "IslandHover"); actions.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; actions.Visibility = Visibility.Collapsed; };
        return row;
    }

    private Button BuildNewTimerRow()
    {
        var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        ring.SetResourceReference(Border.BorderBrushProperty, "IslandTrack");
        var plus = new Path { Data = PlusGeometry, Width = 11, Height = 11, Stretch = Stretch.Uniform };
        plus.SetResourceReference(Shape.FillProperty, "IslandMuted");
        ring.Child = plus;
        var label = new TextBlock { Text = "New timer", FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "IslandMuted");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(ring);
        content.Children.Add(label);
        var button = new Button { Style = (Style)FindResource("RowButton"), Content = content };
        Label(button, "New timer", "NewTimer");
        button.Click += OnNewTimer;
        return button;
    }

    private void OnNewTimer(object sender, RoutedEventArgs e) => StartTyping();

    private Button SmallButton(string geometryKey, string name, string id)
    {
        var path = new Path { Style = (Style)FindResource("SmallIcon"), Data = (Geometry)FindResource(geometryKey) };
        var button = new Button { Style = (Style)FindResource("SmallIconButton"), Content = new Viewbox { Width = 13, Height = 13, Child = path } };
        Label(button, name, id);
        return button;
    }

    private const string ResetGeometry = "ResetGeometry", CrossGeometry = "CrossGeometry", CheckGeometry = "CheckGeometry";

    /// <summary>Gives a control built in code its accessible name and automation id.</summary>
    private static void Label(UIElement element, string name, string id)
    {
        System.Windows.Automation.AutomationProperties.SetName(element, name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
    }

    /// <summary>
    /// Over the list the wheel scrolls it and never changes the volume (spec, design page version 7); a short list
    /// simply does not move. Elsewhere on the island the wheel still changes the volume.
    /// </summary>
    private void OnTimerListWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (TimerListScroll.ScrollableHeight > 0)
            TimerListScroll.ScrollToVerticalOffset(TimerListScroll.VerticalOffset - e.Delta / 120.0 * 29);
    }

    private string? _shuffleKey, _repeatKey;

    private void UpdateToggleColours()
    {
        string shuffle = _nowPlaying.ShuffleOn == true ? "IslandAccent" : "IslandMuted";
        string repeat = _nowPlaying.Repeat is RepeatMode.Track or RepeatMode.List ? "IslandAccent" : "IslandMuted";
        if (shuffle != _shuffleKey) ShuffleButton.SetResourceReference(ForegroundProperty, _shuffleKey = shuffle);
        if (repeat != _repeatKey) RepeatButton.SetResourceReference(ForegroundProperty, _repeatKey = repeat);
    }

    /// <summary>Hue of the album art's average colour, or null when it is too grey.</summary>
    private static double? HueOf(BitmapSource art)
    {
        try
        {
            BitmapSource pixels = new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0);
            pixels = new TransformedBitmap(pixels, new ScaleTransform(16.0 / pixels.PixelWidth, 16.0 / pixels.PixelHeight));
            int stride = pixels.PixelWidth * 4;
            var buffer = new byte[stride * pixels.PixelHeight];
            pixels.CopyPixels(buffer, stride, 0);
            return ThemePalette.ArtHue(buffer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    // ----- controls -----

    private void OnPlayPause(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void OnNext(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    private void OnPrevious(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();
    private void OnShuffle(object sender, RoutedEventArgs e) => ShuffleRequested?.Invoke();
    private void OnRepeat(object sender, RoutedEventArgs e) => RepeatRequested?.Invoke();
    private void OnSwitchApp(object sender, RoutedEventArgs e) => SwitchAppRequested?.Invoke();
    private void OnMute(object sender, RoutedEventArgs e) => MuteToggleRequested?.Invoke();

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MenuRequested?.Invoke();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        VolumeWheel?.Invoke(e.Delta);
    }

    // ----- volume -----

    /// <summary>Shows the Windows volume (0 to 1) and mute; hides the row when no output device can be read.</summary>
    internal void ShowVolume(double level, bool muted, bool available)
    {
        _volumeMuted = muted;
        if (!_volumeDragging) _volumeLevel = level;
        VolumeRow.Visibility = available ? Visibility.Visible : Visibility.Hidden;
        RenderVolume();
    }

    private void RenderVolume()
    {
        double shown = _volumeMuted ? 0 : _volumeLevel;
        double width = VolumeTrack.ActualWidth * Math.Clamp(shown, 0, 1);
        VolumeFill.Width = width;
        VolumeKnob.Margin = new Thickness(width - VolumeKnob.Width / 2, 0, 0, 0);
        VolumePercent.Text = _volumeMuted ? "Muted" : $"{Math.Round(_volumeLevel * 100)}%";
        SpeakerWaves.Data = Platform.VolumeMath.Icon(_volumeLevel, _volumeMuted) switch
        {
            Platform.SpeakerLevel.Muted => WavesMuted,
            Platform.SpeakerLevel.High => WavesHigh,
            Platform.SpeakerLevel.Low => WavesLow,
            _ => null,
        };
        MuteButton.ToolTip = _volumeMuted ? "Unmute" : "Mute";
    }

    private void SetVolumeHover(bool on)
    {
        VolumeRail.Height = VolumeFill.Height = on ? 6 : 4;
        Animate(VolumeKnobScale, ScaleTransform.ScaleXProperty, on ? 1 : 0, 150, CssEase);
        Animate(VolumeKnobScale, ScaleTransform.ScaleYProperty, on ? 1 : 0, 150, CssEase);
    }

    private void OnVolumeDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _volumeDragging = true;
        VolumeTrack.CaptureMouse();
        UpdateVolumeDrag(e);
    }

    private void OnVolumeMove(object sender, MouseEventArgs e)
    {
        if (_volumeDragging && e.LeftButton == MouseButtonState.Pressed) UpdateVolumeDrag(e);
    }

    private void OnVolumeUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_volumeDragging) return;
        UpdateVolumeDrag(e);
        _volumeDragging = false;
        if (VolumeTrack.IsMouseCaptured) VolumeTrack.ReleaseMouseCapture();
        SetVolumeHover(VolumeTrack.IsMouseOver);
        QueuePointerCheck();
    }

    /// <summary>The volume follows the drag live, like the taskbar slider.</summary>
    private void UpdateVolumeDrag(MouseEventArgs e)
    {
        double width = Math.Max(1, VolumeTrack.ActualWidth);
        _volumeLevel = Math.Clamp(e.GetPosition(VolumeTrack).X / width, 0, 1);
        _volumeMuted = false;
        RenderVolume();
        VolumeChangeRequested?.Invoke(_volumeLevel);
    }

    // ----- seek bar -----

    private void UpdateSeekTimer()
    {
        bool run = MusicShown && _nowPlaying.IsPlaying && _nowPlaying.Timeline is not null;
        if (run && !_seekTimer.IsEnabled) _seekTimer.Start();
        else if (!run && _seekTimer.IsEnabled) _seekTimer.Stop();
    }

    private void UpdateSeek()
    {
        // The seek bar is only seen on the open music page; moving it while hidden would still redraw the whole window.
        if (!MusicShown && !_dragging) return;
        var timeline = _nowPlaying.Timeline;
        if (timeline is null) return;
        var position = _dragging
            ? timeline.Duration * _dragFraction
            : TimelineInterpolator.CurrentPosition(timeline, _nowPlaying.IsPlaying, DateTimeOffset.Now) ?? TimeSpan.Zero;
        double fraction = Math.Clamp(position / timeline.Duration, 0, 1);
        double width = SeekTrack.ActualWidth * fraction;
        SeekFill.Width = width;
        SeekKnob.Margin = new Thickness(width - SeekKnob.Width / 2, 0, 0, 0);
        Elapsed.Text = FormatTime(position);
        Remaining.Text = "-" + FormatTime(timeline.Duration - position);
    }

    private static string FormatTime(TimeSpan t)
    {
        int seconds = Math.Max(0, (int)t.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private void SetSeekHover(bool on)
    {
        bool active = on && _nowPlaying.Can.Seek;
        SeekRail.Height = SeekFill.Height = active ? 7 : 5;
        Animate(SeekKnobScale, ScaleTransform.ScaleXProperty, active ? 1 : 0, 150, CssEase);
        Animate(SeekKnobScale, ScaleTransform.ScaleYProperty, active ? 1 : 0, 150, CssEase);
    }

    private void OnSeekDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_nowPlaying.Can.Seek || _nowPlaying.Timeline is null) return;
        _dragging = true;
        SeekTrack.CaptureMouse();
        UpdateDrag(e);
    }

    private void OnSeekMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) UpdateDrag(e);
    }

    private void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_dragging) return;
        UpdateDrag(e);
        EndSeekDrag(commit: true);
    }

    /// <summary>Ends a seek drag; only a real button release seeks, a lost capture just stops.</summary>
    private void EndSeekDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        if (SeekTrack.IsMouseCaptured) SeekTrack.ReleaseMouseCapture();
        SetSeekHover(SeekTrack.IsMouseOver);
        if (commit && _nowPlaying.Timeline is { } timeline) SeekRequested?.Invoke(timeline.Duration * _dragFraction);
        UpdateSeek();
        QueuePointerCheck();
    }

    private void UpdateDrag(MouseEventArgs e)
    {
        double width = Math.Max(1, SeekTrack.ActualWidth);
        _dragFraction = Math.Clamp(e.GetPosition(SeekTrack).X / width, 0, 1);
        UpdateSeek();
    }

    // ----- state and motion -----

    /// <summary>
    /// Moves the island to a state, animated like the design page unless animations are off. When open, it shows
    /// <paramref name="page"/>; changing only the page swaps the layers and keeps the size.
    /// </summary>
    internal void ShowState(IslandState state, IslandPage page)
    {
        bool fromOrToHidden = (state == IslandState.Hidden) != (_state == IslandState.Hidden);
        _state = state;
        _page = page;
        if (!TimersShown && (_topConfirm is not null || _rowConfirm is not null))
        {
            _topConfirm = _rowConfirm = null;
            RenderTimerList(DateTimeOffset.Now);
        }

        var (w, h) = state switch
        {
            IslandState.Hidden or IslandState.Tucked => (120.0, 6.0),
            IslandState.TimerPill => (120.0, 30.0),
            IslandState.Compact => (236.0, 38.0),
            IslandState.Peek => (340.0, 54.0),
            _ => (ExpandedWidth, ExpandedHeight),
        };

        bool notch = _settings.Shape == IslandShape.Notch;
        double rTop, rBottom, shift;
        if (state is IslandState.Hidden or IslandState.Tucked)
        {
            rTop = 0;
            rBottom = 5;
            shift = state == IslandState.Hidden ? -(h + 30) : 0;
        }
        else
        {
            double baseRadius = state == IslandState.Expanded ? 34 : h / 2;
            double r = Math.Max(8, baseRadius * _settings.CornerRoundness / 100.0);
            rTop = notch ? 0 : r;
            rBottom = r;
            shift = notch ? 0 : 10;
        }

        double area = w * h;
        bool grow = area >= _prevArea;
        _prevArea = area;

        Animate(this, IslandWidthProperty, w, grow ? 560 : 340, grow ? SizeSpring : Shrink);
        Animate(this, IslandHeightProperty, h, grow ? 560 : 340, grow ? SizeSpring : Shrink);
        Animate(this, RadiusTopProperty, rTop, grow ? 500 : 340, grow ? RadiusSpring : Shrink);
        Animate(this, RadiusBottomProperty, rBottom, grow ? 500 : 340, grow ? RadiusSpring : Shrink);
        if (fromOrToHidden)
            Animate(this, IslandShiftProperty, shift, grow ? 450 : 360, grow ? ShiftSpring : Shrink);
        else
            Animate(this, IslandShiftProperty, shift, grow ? 400 : 300, CssEase);
        Animate(Body, OpacityProperty, state == IslandState.Hidden ? 0 : 1, 250, CssEase);
        Body.IsHitTestVisible = state != IslandState.Hidden;
        HotStrip.Visibility = state == IslandState.Tucked ? Visibility.Visible : Visibility.Collapsed;

        ShowLayer(TuckLayer, state == IslandState.Tucked);
        ShowLayer(TimerPillLayer, state == IslandState.TimerPill);
        ShowLayer(CompactLayer, state == IslandState.Compact);
        ShowLayer(PeekLayer, state == IslandState.Peek);
        ShowLayer(ExpandedLayer, MusicShown);
        ShowLayer(TimersLayer, TimersShown);
        if (_timerBook is not null)
        {
            RenderTimersPage(DateTimeOffset.Now);
            UpdateTimerTimes(DateTimeOffset.Now); // the pill or chip may have missed ticks while hidden
        }
        // Leaving the Timers page (the song chip, Hidden, full screen) ends typing: a hidden text box must not keep
        // the keyboard. Deferred so it does not run inside the caller's state change.
        if (_typing && !TimersShown)
            Dispatcher.BeginInvoke(() => { if (_typing && !TimersShown) FinishTyping(keep: false); });

        UpdateEqualizers();
        UpdateSeek();
        UpdateSeekTimer();
        _pointerRecheck.Stop();
        _pointerRecheck.Start();
    }

    private void ShowLayer(Grid layer, bool on)
    {
        layer.IsHitTestVisible = on;
        var scale = (ScaleTransform)layer.RenderTransform;
        if (on)
        {
            Animate(layer, OpacityProperty, 1, 280, CssEase, delayMs: 120);
            Animate(scale, ScaleTransform.ScaleXProperty, 1, 500, LayerSpring, delayMs: 60);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, 500, LayerSpring, delayMs: 60);
        }
        else
        {
            Animate(layer, OpacityProperty, 0, 140, CssEase);
            Animate(scale, ScaleTransform.ScaleXProperty, 0.94, 200, CssEase);
            Animate(scale, ScaleTransform.ScaleYProperty, 0.94, 200, CssEase);
        }
    }

    private void Animate(IAnimatable target, DependencyProperty property, double to, int ms, IEasingFunction ease, int delayMs = 0)
    {
        if (!_animate)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        animation.Freeze();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DependencyProperty ShapeProperty(string name, double initial) =>
        DependencyProperty.Register(name, typeof(double), typeof(IslandWindow),
            new PropertyMetadata(initial, (d, _) => ((IslandWindow)d).UpdateShape()));

    /// <summary>Applies the animated size, corners and shift to the island and rebuilds its clip.</summary>
    private void UpdateShape()
    {
        if (Body is null) return;
        double w = Math.Max(1, IslandWidth), h = Math.Max(1, IslandHeight);
        Body.Width = w;
        Body.Height = h;
        var shape = BuildShape(w, h, RadiusTop, RadiusBottom);
        Body.Clip = shape;
        Hairline.Data = shape;
        BodyShift.Y = IslandShift;
    }

    /// <summary>A rectangle with one radius for both top corners and one for both bottom corners.</summary>
    internal static Geometry BuildShape(double w, double h, double rTop, double rBottom)
    {
        double max = Math.Min(w, h) / 2;
        rTop = Math.Clamp(rTop, 0, max);
        rBottom = Math.Clamp(rBottom, 0, max);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(rTop, 0), isFilled: true, isClosed: true);
            c.LineTo(new Point(w - rTop, 0), true, false);
            if (rTop > 0) c.ArcTo(new Point(w, rTop), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(w, h - rBottom), true, false);
            if (rBottom > 0) c.ArcTo(new Point(w - rBottom, h), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(rBottom, h), true, false);
            if (rBottom > 0) c.ArcTo(new Point(0, h - rBottom), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(0, rTop), true, false);
            if (rTop > 0) c.ArcTo(new Point(rTop, 0), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // ----- equalizer -----

    private static void BuildEqualizer(Panel panel, double height, double gap)
    {
        panel.Children.Clear();
        for (int i = 0; i < EqRatios.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = height,
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(1, EqRatios[i]),
            };
            bar.SetResourceReference(Shape.FillProperty, "IslandAccent");
            panel.Children.Add(bar);
        }
    }

    /// <summary>
    /// Bounces the bars of the visible layer only while music plays and animations are on.
    /// Everywhere else the bars stand still, so nothing animates while the island is idle.
    /// </summary>
    private void UpdateEqualizers()
    {
        bool playing = _nowPlaying.IsPlaying;
        foreach (var (panel, owner) in new[] { (CompactEq, IslandState.Compact), (PeekEq, IslandState.Peek), (ExpandedEq, IslandState.Expanded) })
        {
            bool running = playing && _animate && panel.Visibility == Visibility.Visible && _state == owner
                && (owner != IslandState.Expanded || _page == IslandPage.Music);
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var bar = (Rectangle)panel.Children[i];
                var scale = (ScaleTransform)bar.RenderTransform;
                if (running)
                {
                    if (bar.Tag is true) continue;
                    var bounce = new DoubleAnimation(0.28, 1.0, TimeSpan.FromSeconds(EqSeconds[i] / 2))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    // The see-through window redraws in full on every frame, so the decorative bars run at 30 fps.
                    Timeline.SetDesiredFrameRate(bounce, 30);
                    bounce.Freeze();
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
                    bar.Opacity = 1;
                    bar.Tag = true;
                }
                else
                {
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleY = playing ? EqRatios[i] : 0.22;
                    bar.Opacity = playing ? 1 : 0.55;
                    bar.Tag = false;
                }
            }
        }
    }

    // ----- pointer -----

    private void QueuePointerCheck() => Dispatcher.BeginInvoke(DispatcherPriority.Input, EvaluatePointer);

    /// <summary>
    /// Decides whether the pointer is on the island by hit-testing the real cursor position, so a pair of
    /// leave/enter events (strip to island) or an island resizing under a still cursor is judged correctly.
    /// While the seek bar is being dragged the pointer counts as on the island.
    /// </summary>
    private void EvaluatePointer()
    {
        if (MenuOpen) return;
        bool over = _dragging || _volumeDragging;
        if (!over && _hwnd != IntPtr.Zero && GetCursorPos(out var cursor)
            && GetAncestor(WindowFromPoint(cursor), GA_ROOT) == _hwnd)
        {
            var local = PointFromScreen(new Point(cursor.X, cursor.Y));
            if (InputHitTest(local) is DependencyObject hit)
                over = IsWithin(hit, Body) || IsWithin(hit, HotStrip);
        }
        if (over == _pointerOver) return;
        _pointerOver = over;
        (over ? PointerEnteredIsland : PointerLeftIsland)?.Invoke();
    }

    private void OnIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsOnControl(source)) return;
        BackgroundClicked?.Invoke();
    }

    /// <summary>True for a click on a button or on an element tagged "control" (the seek bar).</summary>
    private bool IsOnControl(DependencyObject d)
    {
        for (DependencyObject? node = d; node is not null && node != Body; node = ParentOf(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (node is FrameworkElement { Tag: "control" }) return true;
        }
        return false;
    }

    private static bool IsWithin(DependencyObject hit, DependencyObject ancestor)
    {
        for (DependencyObject? node = hit; node is not null; node = ParentOf(node))
            if (node == ancestor) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
