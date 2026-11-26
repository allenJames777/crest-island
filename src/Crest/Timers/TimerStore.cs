using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Crest.Settings;

namespace Crest.Timers;

/// <summary>
/// Loads and saves the timers as %APPDATA%\Crest\timers.json (decision D-20261125-crest-timers-own-store).
/// A timer that was running when Crest stopped comes back paused, counted up to the last moment Crest saved,
/// so a crash loses at most a minute. A file that cannot be read is kept as timers.bad.json.
/// </summary>
public sealed class TimerStore
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IDebounceScheduler _scheduler;
    private IReadOnlyList<ActivityTimer> _timers = [];
    private DateTimeOffset? _lastSeen;
    private IDisposable? _pendingSave;

    public TimerStore(string path, IDebounceScheduler scheduler)
    {
        _path = path;
        _scheduler = scheduler;
    }

    /// <summary>%APPDATA%\Crest\timers.json</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crest", "timers.json");

    public string BadFilePath => Path.Combine(Path.GetDirectoryName(_path)!, "timers.bad.json");

    /// <summary>The saved timers, all paused.</summary>
    public IReadOnlyList<ActivityTimer> Load()
    {
        var file = ReadFile();
        _lastSeen = file?.LastSeen;
        _timers = file is null ? [] : PauseRunning(Usable(file.Timers), file.LastSeen);
        return _timers;
    }

    /// <summary>
    /// Keeps only timers Crest can use: a hand-edited or damaged file may hold empty entries, a missing name or id,
    /// the same id twice, or a negative total. Names are cleaned as when typed.
    /// </summary>
    private static List<ActivityTimer> Usable(List<ActivityTimer?>? timers)
    {
        var seen = new HashSet<Guid>();
        var usable = new List<ActivityTimer>();
        foreach (var t in timers ?? [])
        {
            if (t is null || t.Id == Guid.Empty || !seen.Add(t.Id)) continue;
            if (TimerBook.CleanName(t.Name) is not { } name) continue;
            usable.Add(t with { Name = name, Accumulated = t.Accumulated < TimeSpan.Zero ? TimeSpan.Zero : t.Accumulated });
        }
        return usable;
    }

    /// <summary>Keeps <paramref name="timers"/> and writes them 300 ms after the last change.</summary>
    public void Save(IReadOnlyList<ActivityTimer> timers, DateTimeOffset now)
    {
        _timers = timers.ToList();
        _lastSeen = now;
        _pendingSave?.Dispose();
        _pendingSave = _scheduler.Schedule(SaveDelay, () =>
        {
            _pendingSave = null;
            Write();
        });
    }

    /// <summary>Records that Crest was still running at <paramref name="now"/> (called once a minute while a timer runs).</summary>
    public void SaveLastSeen(DateTimeOffset now)
    {
        _lastSeen = now;
        _pendingSave?.Dispose();
        _pendingSave = null;
        Write();
    }

    /// <summary>Writes any pending save now (used on sleep and exit).</summary>
    public void Flush()
    {
        if (_pendingSave is null) return;
        _pendingSave.Dispose();
        _pendingSave = null;
        Write();
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new TimerFile(_timers.ToList<ActivityTimer?>(), _lastSeen), Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Crest: could not save timers: {ex.Message}");
        }
    }

    /// <summary>
    /// Every timer comes back paused. With more than one running, the earlier ones stop when the latest started;
    /// the latest counts up to <paramref name="lastSeen"/> when that is after its start, and otherwise counts nothing.
    /// </summary>
    private static List<ActivityTimer> PauseRunning(List<ActivityTimer> timers, DateTimeOffset? lastSeen)
    {
        var latest = timers.Where(t => t.IsRunning).MaxBy(t => t.RunningSince);
        return timers.Select(t =>
        {
            if (!t.IsRunning) return t;
            DateTimeOffset end = t.Id == latest!.Id ? lastSeen ?? t.RunningSince!.Value : latest.RunningSince!.Value;
            return t.PausedAt(end);
        }).ToList();
    }

    private TimerFile? ReadFile()
    {
        string text;
        try
        {
            if (!File.Exists(_path)) return null;
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Crest: could not read timers: {ex.Message}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TimerFile>(text, Json);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Crest: timers file is not valid, starting with none: {ex.Message}");
            try { File.Move(_path, BadFilePath, overwrite: true); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Crest: could not keep the bad timers file: {moveEx.Message}");
            }
            return null;
        }
    }

    private sealed record TimerFile(List<ActivityTimer?>? Timers, DateTimeOffset? LastSeen);
}
