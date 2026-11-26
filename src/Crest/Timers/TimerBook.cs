using System.Text.Json.Serialization;

namespace Crest.Timers;

/// <summary>One named count-up timer. Its total is <see cref="Accumulated"/> plus the time since <see cref="RunningSince"/>.</summary>
public sealed record ActivityTimer(Guid Id, string Name, TimeSpan Accumulated, DateTimeOffset? RunningSince)
{
    [JsonIgnore]
    public bool IsRunning => RunningSince is not null;

    /// <summary>The total at <paramref name="now"/>, counted from timestamps rather than ticks.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        RunningSince is { } since && now > since ? Accumulated + (now - since) : Accumulated;

    /// <summary>This timer stopped at <paramref name="at"/> (never counting time before it started).</summary>
    public ActivityTimer PausedAt(DateTimeOffset at) =>
        IsRunning ? this with { Accumulated = Elapsed(at), RunningSince = null } : this;
}

/// <summary>
/// The timers and their one rule: only one runs at a time (decision D-20261125-crest-timers-one-running).
/// Pure logic: the caller passes the time in.
/// </summary>
public sealed class TimerBook
{
    public const int MaxNameLength = 40;

    private readonly List<ActivityTimer> _timers;
    private Guid? _lastUsed;

    public TimerBook(IEnumerable<ActivityTimer>? timers = null)
    {
        _timers = timers?.ToList() ?? [];
        // A file with more than one running timer keeps only the latest running.
        var running = _timers.Where(t => t.IsRunning).OrderBy(t => t.RunningSince).ToList();
        if (running.Count > 1)
        {
            var keep = running[^1];
            for (int i = 0; i < _timers.Count; i++)
                if (_timers[i].IsRunning && _timers[i].Id != keep.Id)
                    _timers[i] = _timers[i].PausedAt(keep.RunningSince!.Value);
        }
    }

    /// <summary>Raised once after every change.</summary>
    public event Action? Changed;

    /// <summary>All timers, in the order they were added.</summary>
    public IReadOnlyList<ActivityTimer> Timers => _timers;

    public ActivityTimer? Running => _timers.FirstOrDefault(t => t.IsRunning);

    /// <summary>The timer in the Timers page's top row: the running one, else the last one started or paused, else the last in the list.</summary>
    public ActivityTimer? Top => Running ?? (_lastUsed is { } id ? Find(id) : null) ?? _timers.LastOrDefault();

    /// <summary>Every timer except <see cref="Top"/>, in the order they were added.</summary>
    public IReadOnlyList<ActivityTimer> Others
    {
        get
        {
            var top = Top;
            return _timers.Where(t => t.Id != top?.Id).ToList();
        }
    }

    public ActivityTimer? Find(Guid id) => _timers.FirstOrDefault(t => t.Id == id);

    public TimeSpan Elapsed(Guid id, DateTimeOffset now) => Find(id)?.Elapsed(now) ?? TimeSpan.Zero;

    /// <summary>The name as it will be kept: trimmed and cut to <see cref="MaxNameLength"/>, or null when empty.</summary>
    public static string? CleanName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    /// <summary>Adds a timer at the end of the list and starts it. An empty name adds nothing and returns null.</summary>
    public ActivityTimer? Add(string? name, DateTimeOffset now)
    {
        var clean = CleanName(name);
        if (clean is null) return null;
        var timer = new ActivityTimer(Guid.NewGuid(), clean, TimeSpan.Zero, null);
        _timers.Add(timer);
        StartCore(timer.Id, now);
        Changed?.Invoke();
        return Find(timer.Id);
    }

    /// <summary>Starts a timer and pauses the one that was running.</summary>
    public void Start(Guid id, DateTimeOffset now)
    {
        if (Find(id) is null) return;
        StartCore(id, now);
        Changed?.Invoke();
    }

    public void Pause(Guid id, DateTimeOffset now)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        _timers[i] = _timers[i].PausedAt(now);
        _lastUsed = id;
        Changed?.Invoke();
    }

    /// <summary>Sets the total to zero. A running timer keeps running from zero.</summary>
    public void Reset(Guid id, DateTimeOffset now)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        _timers[i] = _timers[i] with { Accumulated = TimeSpan.Zero, RunningSince = _timers[i].IsRunning ? now : null };
        Changed?.Invoke();
    }

    public void Delete(Guid id)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        _timers.RemoveAt(i);
        if (_lastUsed == id) _lastUsed = null;
        Changed?.Invoke();
    }

    /// <summary>Pauses the running timer at <paramref name="at"/> (for example the moment the PC went to sleep).</summary>
    public void PauseAll(DateTimeOffset at)
    {
        bool any = false;
        for (int i = 0; i < _timers.Count; i++)
        {
            if (!_timers[i].IsRunning) continue;
            _timers[i] = _timers[i].PausedAt(at);
            _lastUsed = _timers[i].Id;
            any = true;
        }
        if (any) Changed?.Invoke();
    }

    private void StartCore(Guid id, DateTimeOffset now)
    {
        for (int i = 0; i < _timers.Count; i++)
        {
            var t = _timers[i];
            if (t.Id == id)
            {
                if (!t.IsRunning) _timers[i] = t with { RunningSince = now };
            }
            else if (t.IsRunning)
            {
                _timers[i] = t.PausedAt(now);
            }
        }
        _lastUsed = id;
    }

    private int IndexOf(Guid id) => _timers.FindIndex(t => t.Id == id);
}
