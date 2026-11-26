using System.Windows;
using System.Windows.Threading;
using Crest.Island;
using Crest.Media;
using Crest.Settings;

namespace Crest;

/// <summary>
/// Crest runs without a main window. Services, the state machine and the island window are wired here:
/// every input goes to the state machine, and the island shows whatever state it decides.
/// </summary>
public partial class App : Application
{
    private IslandWindow? _island;
    private MediaService? _media;
    private Platform.SystemVolume? _volume;
    private Platform.FullScreenWatcher? _fullScreen;
    private Tray.TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DateTimeOffset _lastTrim = DateTimeOffset.MinValue;
    private Mutex? _singleInstance;
    private EventWaitHandle? _openSettingsSignal;
    private RegisteredWaitHandle? _openSettingsWait;
    private SettingsStore? _settings;
    private IslandStateMachine? _machine;
    private DispatcherTimer? _wake;
    private string _lastTrackKey = "";
    private Timers.TimerStore? _timerStore;
    private Timers.TimerBook? _timers;

    private static DateTimeOffset Now => DateTimeOffset.Now;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One Crest at a time: a second launch asks the first one to open Settings, then exits.
        // The signal is created (or opened) before the mutex, so a second launch can always reach it.
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Crest.OpenSettings");
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Crest.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            _openSettingsSignal.Set();
            _openSettingsSignal.Dispose();
            _openSettingsSignal = null;
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;
        _openSettingsWait = ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => Dispatcher.InvokeAsync(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        // The island is a small see-through (layered) window. Drawing it on the CPU skips the GPU device and the
        // per-frame copy back from the GPU, which costs less memory and CPU for a window this size.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        _settings = new SettingsStore(SettingsStore.DefaultPath, new DispatcherDebounceScheduler(Dispatcher));
        _settings.Load();
        _machine = new IslandStateMachine(_settings.Current, Now);

        // Timers (spec 2026-11-25-crest-timers): saved beside the settings; a timer that was running comes back paused.
        _timerStore = new Timers.TimerStore(Timers.TimerStore.DefaultPath, new DispatcherDebounceScheduler(Dispatcher));
        _timers = new Timers.TimerBook(_timerStore.Load());
        _machine.SetTimerRunning(_timers.Running is not null, Now);

        // One timer, running only while the state machine has a pending wake-up.
        _wake = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _wake.Tick += (_, _) =>
        {
            _wake.Stop();
            _machine.Tick(Now);
            Sync();
        };

        _island = new IslandWindow { WindowsAnimationsOn = Platform.AnimationPreference.WindowsAnimationsOn() };
        _island.ApplySettings(_settings.Current);
        _island.ShowState(_machine.State, _machine.Page);
        _island.PointerEnteredIsland += () => { _machine.PointerEntered(Now); Sync(); };
        _island.PointerLeftIsland += () => { _machine.PointerLeft(Now); Sync(); };
        _island.BackgroundClicked += () => { _machine.ClickedBackground(Now); Sync(); };
        _island.ShowTimers(_timers, Now);
        _island.PageRequested += page => { _machine.ShowPage(page); Sync(); };
        _island.TimerToggleRequested += id =>
        {
            if (_timers.Find(id) is { IsRunning: true }) _timers.Pause(id, Now);
            else _timers.Start(id, Now);
        };
        _island.TimerResetRequested += id => _timers.Reset(id, Now);
        _island.TimerDeleteRequested += id => _timers.Delete(id);
        _island.TimerAddRequested += name => _timers.Add(name, Now);
        _island.TypingStarted += () => { _machine.TypingStarted(Now); Sync(); };
        _island.TypingEnded += () => { _machine.TypingEnded(Now); Sync(); };
        _timers.Changed += OnTimersChanged;
        // Signing out or shutting down pauses the running timer (decision D-20261125-crest-timers-pause-on-sleep).
        SessionEnding += (_, _) =>
        {
            _timers.PauseAll(Now);
            _timerStore.Flush();
        };
        _island.Show();

        _settings.Changed += OnSettingsChanged;

        _media = new MediaService(Dispatcher);
        _island.PlayPauseRequested += () => _ = _media.TogglePlayPauseAsync();
        _island.NextRequested += () => _ = _media.NextAsync();
        _island.PreviousRequested += () => _ = _media.PreviousAsync();
        _island.ShuffleRequested += () => _ = _media.ToggleShuffleAsync();
        _island.RepeatRequested += () => _ = _media.CycleRepeatAsync();
        _island.SeekRequested += position => _ = _media.SeekAsync(position);
        _island.SwitchAppRequested += () => _media.ShowNextApp();

        _volume = new Platform.SystemVolume(Dispatcher);
        _volume.Changed += (level, muted) => _island.ShowVolume(level, muted, _volume.Available);
        _island.ShowVolume(_volume.Level, _volume.Muted, _volume.Available);
        _island.VolumeChangeRequested += level => _volume.SetLevel(level);
        _island.MuteToggleRequested += () => _volume.ToggleMute();
        _island.VolumeWheel += delta => _volume.Step(delta);

        // Full screen on the island's display, and staying above other topmost windows.
        _fullScreen = new Platform.FullScreenWatcher(() => _island.Handle);
        _fullScreen.Changed += on => { _machine.SetFullScreen(on, Now); Sync(); };
        _fullScreen.ForegroundChanged += _island.KeepOnTop;
        _machine.SetFullScreen(_fullScreen.IsFullScreen, Now);
        Sync();

        // Windows raises these on their own thread; handle them on the UI thread.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Give memory used while starting up back to Windows once things settle.
        var settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        settle.Tick += (_, _) => { settle.Stop(); TrimMemory(); };
        settle.Start();

        // Tray icon and menu (also opened by a right-click on the island).
        _tray = new Tray.TrayIcon();
        _tray.Update(_settings.Current.IslandVisible, _settings.Current.StartWithWindows);
        _tray.ShowHideClicked += () => _settings.Update(s => s with { IslandVisible = !s.IslandVisible });
        _tray.SettingsClicked += OpenSettings;
        _tray.StartupClicked += () => _settings.Update(s => s with { StartWithWindows = !s.StartWithWindows });
        _tray.QuitClicked += Shutdown;
        _island.MenuRequested += () => { _island.MenuOpen = true; _tray.ShowMenuAtCursor(); };
        _tray.MenuClosed += () => { _island.MenuOpen = false; _island.RecheckPointer(); };

        // The setting is the source of truth for start with Windows; on, the Run value points at this exe.
        if (_settings.Current.StartWithWindows || Platform.StartupRegistration.IsEnabled())
            Platform.StartupRegistration.Set(_settings.Current.StartWithWindows);
        _media.Changed += OnNowPlaying;
        _ = _media.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _openSettingsWait?.Unregister(null);
        _openSettingsSignal?.Dispose();
        _tray?.Dispose();
        _fullScreen?.Dispose();
        _wake?.Stop();
        _media?.Dispose();
        _volume?.Dispose();
        _settings?.Flush();
        // Quitting pauses the running timer (decision D-20261125-crest-timers-pause-on-sleep).
        _minuteClock?.Stop();
        _secondClock?.Stop();
        if (_timers is not null && _timerStore is not null)
        {
            _timers.Changed -= OnTimersChanged;
            _timers.PauseAll(Now);
            _timerStore.Save(_timers.Timers, Now);
            _timerStore.Flush();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _island?.PlaceOnDisplay();
        _fullScreen?.Recheck();
    });

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.InvokeAsync(() => _media?.Refresh());
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) PauseTimersForSleep(DateTimeOffset.Now);
    }

    /// <summary>
    /// The PC is going to sleep: pause the running timer at <paramref name="at"/>, taken in the handler, and write the
    /// file now. This runs on a SystemEvents thread, so the work is sent to the UI thread and waited for (up to 1.5 s);
    /// queued work could run only after waking and count the whole sleep.
    /// </summary>
    internal void PauseTimersForSleep(DateTimeOffset at)
    {
        var pause = Dispatcher.InvokeAsync(() =>
        {
            _timers?.PauseAll(at);
            _timerStore?.Flush();
        }, DispatcherPriority.Send);
        // A timed-out wait does not throw: check that the pause ran, and drop it if the UI thread never got to it.
        var status = pause.Wait(TimeSpan.FromMilliseconds(1500));
        if (status != DispatcherOperationStatus.Completed)
        {
            pause.Abort();
            System.Diagnostics.Debug.WriteLine($"Crest: could not pause timers before sleep ({status}).");
        }
    }

    // ----- the two timer clocks (decision D-20261125-crest-timers-clock) -----

    private DispatcherTimer? _minuteClock;
    private DispatcherTimer? _secondClock;

    /// <summary>
    /// Runs the minute clock while a timer runs (pill text and the LastSeen save), and the second clock only while the
    /// island is open on a running time. Both stop when no timer runs.
    /// </summary>
    private void UpdateClocks()
    {
        var running = _timers?.Running;
        if (running is null)
        {
            _minuteClock?.Stop();
            _secondClock?.Stop();
            return;
        }
        var elapsed = running.Elapsed(Now);
        if (_minuteClock is null)
        {
            _minuteClock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _minuteClock.Tick += (_, _) =>
            {
                _minuteClock.Stop();
                _island?.UpdateTimerTimes(Now);
                _timerStore?.SaveLastSeen(Now);
                UpdateClocks();
            };
        }
        if (!_minuteClock.IsEnabled)
        {
            // Timed to the running timer's next whole minute, so the pill changes when the minute does.
            _minuteClock.Interval = TimeSpan.FromSeconds(60 - elapsed.TotalSeconds % 60) + TimeSpan.FromMilliseconds(20);
            _minuteClock.Start();
        }

        bool open = _machine?.State == IslandState.Expanded;
        if (!open)
        {
            _secondClock?.Stop();
            return;
        }
        if (_secondClock is null)
        {
            _secondClock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _secondClock.Tick += (_, _) =>
            {
                _secondClock.Stop();
                _island?.UpdateTimerTimes(Now);
                UpdateClocks();
            };
        }
        if (!_secondClock.IsEnabled)
        {
            _secondClock.Interval = TimeSpan.FromSeconds(1 - elapsed.TotalSeconds % 1) + TimeSpan.FromMilliseconds(20);
            _secondClock.Start();
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) Dispatcher.InvokeAsync(() => _media?.Refresh());
    }

    /// <summary>
    /// Backstop: an unexpected error in a UI callback is logged and Crest keeps running, rather than the island
    /// vanishing (a media app closing mid-call is the usual cause).
    /// </summary>
    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"Crest: unexpected error: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>Opens the Settings window, or brings it forward. While it is open the island stays Expanded.</summary>
    private void OpenSettings()
    {
        if (_settings is null || _machine is null) return;
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _machine.SettingsClosed(Now);
            Sync();
        };
        _machine.SettingsOpened(Now);
        Sync();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            bool on = Platform.AnimationPreference.WindowsAnimationsOn();
            if (_island is null || _settings is null || on == _island.WindowsAnimationsOn) return;
            _island.WindowsAnimationsOn = on;
            _island.ApplySettings(_settings.Current);
        });

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_tray is not null && settings.StartWithWindows != Platform.StartupRegistration.IsEnabled())
            Platform.StartupRegistration.Set(settings.StartWithWindows);
        _tray?.Update(settings.IslandVisible, settings.StartWithWindows);
        _machine!.UpdateSettings(settings, Now);
        _island!.ApplySettings(settings);
        Sync();
    }

    private void OnNowPlaying(NowPlaying np)
    {
        _island!.ShowNowPlaying(np);
        _machine!.UpdateMedia(np.HasSession, np.IsPlaying, Now);
        if (np.HasSession && np.TrackKey != _lastTrackKey) _machine.TrackChanged(Now);
        _lastTrackKey = np.HasSession ? np.TrackKey : "";
        Sync();
    }

    /// <summary>A timer was added, started, paused, reset or deleted: save, update the rest state and the island.</summary>
    private void OnTimersChanged()
    {
        _timerStore!.Save(_timers!.Timers, Now);
        _machine!.SetTimerRunning(_timers.Running is not null, Now);
        _island!.ShowTimers(_timers, Now);
        // A start, pause or reset moves the next whole minute and second: re-time both clocks.
        _minuteClock?.Stop();
        _secondClock?.Stop();
        Sync();
    }

    /// <summary>Shows the machine's state (or, while open, its page) if it changed, and arms the timer for its next wake-up.</summary>
    private void Sync()
    {
        if (_island!.CurrentPage != _machine!.Page && _island.CurrentState == _machine.State)
            _island.ShowState(_machine.State, _machine.Page);
        if (_island.CurrentState != _machine.State)
        {
            _island.ShowState(_machine.State, _machine.Page);
            // Back at rest: hand memory used while open back to Windows (at most once a minute). The timer pill is the
            // rest state while a timer runs, so without it Crest would never trim during a long timer.
            if (_machine.State is IslandState.Tucked or IslandState.Hidden or IslandState.TimerPill
                && Now - _lastTrim > TimeSpan.FromMinutes(1))
                Dispatcher.InvokeAsync(TrimMemory, DispatcherPriority.ApplicationIdle);
        }

        UpdateClocks();

        _wake!.Stop();
        if (_machine.NextWake is { } wake)
        {
            var delay = wake - Now;
            _wake.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
            _wake.Start();
        }
    }

    /// <summary>
    /// Lets Windows take back memory pages Crest is not using right now (decision D-20261124-crest-trim-memory).
    /// Pages Crest needs again come back on use; this keeps a resting island light.
    /// </summary>
    private void TrimMemory()
    {
        _lastTrim = Now;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Runs debounced actions on the UI thread with a one-shot DispatcherTimer.</summary>
    private sealed class DispatcherDebounceScheduler(Dispatcher dispatcher) : IDebounceScheduler
    {
        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
            return new Cancel(timer);
        }

        private sealed class Cancel(DispatcherTimer timer) : IDisposable
        {
            public void Dispose() => timer.Stop();
        }
    }
}
