using Crest.Settings;

namespace Crest.Island;

public enum IslandState { Hidden, Tucked, Compact, Peek, Expanded, TimerPill }

/// <summary>Which page the open island shows.</summary>
public enum IslandPage { Music, Timers }

/// <summary>
/// Decides the island's state for every input, following the spec's rest-state rules and
/// transition table. Pure logic: the caller passes the time in, reads <see cref="State"/>,
/// and calls <see cref="Tick"/> at <see cref="NextWake"/>.
/// </summary>
public sealed class IslandStateMachine
{
    /// <summary>How long a paused session still counts as playing, so the island does not flicker between tracks.</summary>
    public static readonly TimeSpan NothingPlayingGrace = TimeSpan.FromSeconds(5);

    private AppSettings _settings;
    private bool _hasSession;
    private bool _isPlaying;
    private DateTimeOffset? _notPlayingSince;
    private bool _fullScreen;
    private bool _trayHidden;
    private bool _timerRunning;
    private bool _settingsHeld;
    private bool _typing;

    private DateTimeOffset? _openAt;
    private DateTimeOffset? _collapseAt;
    private DateTimeOffset? _peekEndAt;
    private DateTimeOffset? _graceAt;
    private IslandState _lastRest;

    public IslandStateMachine(AppSettings settings, DateTimeOffset now)
    {
        _settings = settings;
        _trayHidden = !settings.IslandVisible;
        State = _lastRest = ComputeRest(now);
    }

    public IslandState State { get; private set; }

    public bool IsHovered { get; private set; }

    /// <summary>True while the Settings window holds the island open, or while a timer name is being typed.</summary>
    public bool IsHeld => _settingsHeld || _typing;

    /// <summary>The page the open island shows (Music when Crest starts; kept in memory only).</summary>
    public IslandPage Page { get; private set; } = IslandPage.Music;

    /// <summary>When the caller should call <see cref="Tick"/> next, or null when nothing is pending.</summary>
    public DateTimeOffset? NextWake =>
        new[] { _openAt, _collapseAt, _peekEndAt, _graceAt }.Where(t => t.HasValue).Min();

    public IslandState RestState(DateTimeOffset now) => ComputeRest(now);

    // ----- inputs that change the rest state -----

    public void UpdateSettings(AppSettings settings, DateTimeOffset now) =>
        ChangeRestInput(now, () =>
        {
            _settings = settings;
            _trayHidden = !settings.IslandVisible;
        });

    public void UpdateMedia(bool hasSession, bool isPlaying, DateTimeOffset now) =>
        ChangeRestInput(now, () =>
        {
            if (hasSession && !isPlaying && (_isPlaying || !_hasSession))
            {
                _notPlayingSince = now;
                _graceAt = now + NothingPlayingGrace;
            }
            else if (isPlaying || !hasSession)
            {
                _notPlayingSince = null;
                _graceAt = null;
            }
            _hasSession = hasSession;
            _isPlaying = isPlaying;
        });

    public void SetFullScreen(bool fullScreen, DateTimeOffset now) => ChangeRestInput(now, () => _fullScreen = fullScreen);

    public void SetTrayHidden(bool hidden, DateTimeOffset now) => ChangeRestInput(now, () => _trayHidden = hidden);

    /// <summary>Whether a timer is running (decision D-20261125-crest-timers-rest-pill).</summary>
    public void SetTimerRunning(bool running, DateTimeOffset now) => ChangeRestInput(now, () => _timerRunning = running);

    /// <summary>A page chip was clicked while the island is open.</summary>
    public void ShowPage(IslandPage page) => Page = page;

    /// <summary>A timer name is being typed: the island stays open until typing ends.</summary>
    public void TypingStarted(DateTimeOffset now)
    {
        _typing = true;
        _collapseAt = null;
    }

    /// <summary>Typing ended; with the pointer away, the island closes after the usual delay.</summary>
    public void TypingEnded(DateTimeOffset now)
    {
        _typing = false;
        if (!IsHovered && !IsHeld && State != ComputeRest(now))
            _collapseAt = now + TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
    }

    // ----- events -----

    public void PointerEntered(DateTimeOffset now)
    {
        IsHovered = true;
        _collapseAt = null;
        if (State == IslandState.Hidden || IsHeld) return;

        bool opens = _settings.ExpandOn == ExpandOn.Hover
            ? State != IslandState.Expanded
            : State is IslandState.Tucked or IslandState.Peek;
        if (opens) _openAt = now + TimeSpan.FromMilliseconds(_settings.HoverDelayMs);
        ProcessDue(now);
    }

    public void PointerLeft(DateTimeOffset now)
    {
        IsHovered = false;
        _openAt = null;
        if (IsHeld) return;

        bool peekStillRunning = State == IslandState.Peek && _peekEndAt is not null;
        if (State != ComputeRest(now) && !peekStillRunning)
            _collapseAt = now + TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
        ProcessDue(now);
    }

    /// <summary>A click on the island itself, not on one of its buttons or the seek bar.</summary>
    public void ClickedBackground(DateTimeOffset now)
    {
        if (IsHeld) return;
        if (State is IslandState.Tucked or IslandState.Compact or IslandState.Peek or IslandState.TimerPill)
        {
            _openAt = null;
            _peekEndAt = null;
            Open();
        }
        else if (State == IslandState.Expanded && _settings.ExpandOn == ExpandOn.Click)
        {
            GoToRest(now);
        }
    }

    public void TrackChanged(DateTimeOffset now)
    {
        if (!_settings.PeekOnTrackChange || _fullScreen || IsHovered || IsHeld) return;
        if (State is not (IslandState.Tucked or IslandState.Compact or IslandState.Peek or IslandState.TimerPill)) return;
        State = IslandState.Peek;
        _peekEndAt = now + TimeSpan.FromSeconds(_settings.PeekSeconds);
    }

    public void SettingsOpened(DateTimeOffset now)
    {
        _settingsHeld = true;
        _openAt = _collapseAt = _peekEndAt = null;
        State = IslandState.Expanded;
    }

    public void SettingsClosed(DateTimeOffset now)
    {
        _settingsHeld = false;
        GoToRest(now);
    }

    /// <summary>Runs every timer that is due.</summary>
    public void Tick(DateTimeOffset now) => ProcessDue(now);

    // ----- internals -----

    private void ProcessDue(DateTimeOffset now)
    {
        if (_graceAt <= now)
        {
            _graceAt = null;
            ChangeRestInput(now, () => { });
        }
        if (_openAt <= now)
        {
            _openAt = null;
            HoverResolved();
        }
        if (_peekEndAt <= now)
        {
            _peekEndAt = null;
            if (State == IslandState.Peek && !IsHovered) GoToRest(now);
        }
        if (_collapseAt <= now)
        {
            _collapseAt = null;
            if (!IsHovered && !IsHeld) GoToRest(now);
        }
    }

    private void HoverResolved()
    {
        if (_settings.ExpandOn == ExpandOn.Hover)
        {
            if (State is IslandState.Tucked or IslandState.Compact or IslandState.Peek or IslandState.TimerPill)
            {
                _peekEndAt = null;
                Open();
            }
        }
        else if (State is IslandState.Tucked or IslandState.Peek)
        {
            _peekEndAt = null;
            State = IslandState.Compact;
        }
    }

    /// <summary>
    /// Opens the island on what it was showing (decision D-20261125-crest-timers-open-page): the timer pill opens
    /// Timers, a song pill or peek opens Music, the tucked line keeps the page used last.
    /// </summary>
    private void Open()
    {
        Page = State switch
        {
            IslandState.TimerPill => IslandPage.Timers,
            IslandState.Compact or IslandState.Peek => IslandPage.Music,
            _ => Page,
        };
        State = IslandState.Expanded;
    }

    /// <summary>
    /// Applies a change to a rest-state input. When the rest state changes and the pointer is not on
    /// the island (and Settings is not holding it), the island moves to the new rest state.
    /// </summary>
    private void ChangeRestInput(DateTimeOffset now, Action change)
    {
        change();
        var rest = ComputeRest(now);
        if (rest == _lastRest) return;
        _lastRest = rest;
        if (!IsHovered && !IsHeld) GoToRest(now);
    }

    private void GoToRest(DateTimeOffset now)
    {
        _collapseAt = null;
        _peekEndAt = null;
        State = _lastRest = ComputeRest(now);
    }

    private bool NothingPlaying(DateTimeOffset now) =>
        !_hasSession || (!_isPlaying && _notPlayingSince is { } since && now - since >= NothingPlayingGrace);

    private IslandState ComputeRest(DateTimeOffset now)
    {
        if (_trayHidden) return IslandState.Hidden;
        if (_fullScreen)
        {
            if (_settings.OverFullScreen == OverFullScreen.Hide) return IslandState.Hidden;
            if (_settings.OverFullScreen == OverFullScreen.Tuck) return IslandState.Tucked;
        }
        // A running timer is something to show, so it comes before the music rules.
        if (_timerRunning && _settings.WhileTimerRuns == WhileTimerRuns.TimerPill) return IslandState.TimerPill;
        if (NothingPlaying(now))
        {
            return _settings.WhenNothingPlaying switch
            {
                WhenNothingPlaying.KeepPill => IslandState.Compact,
                WhenNothingPlaying.Hide => IslandState.Hidden,
                _ => IslandState.Tucked,
            };
        }
        return _settings.AutoTuckWhilePlaying ? IslandState.Tucked : IslandState.Compact;
    }
}
