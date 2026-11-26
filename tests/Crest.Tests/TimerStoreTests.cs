using System.IO;
using Crest.Settings;
using Crest.Timers;

namespace Crest.Tests;

public sealed class TimerStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crest-timer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScheduler _scheduler = new();

    private string FilePath => Path.Combine(_dir, "timers.json");

    public TimerStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private TimerStore NewStore() => new(FilePath, _scheduler);

    private static ActivityTimer Timer(string name, double minutes, DateTimeOffset? runningSince = null) =>
        new(Guid.NewGuid(), name, TimeSpan.FromMinutes(minutes), runningSince);

    [Fact]
    public void A_missing_file_gives_no_timers()
    {
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void Paused_timers_load_back_the_same()
    {
        var timers = new[] { Timer("Studying", 42), Timer("Reading", 15) };
        var store = NewStore();
        store.Save(timers, T0);
        _scheduler.RunPending();

        Assert.Equal(timers, NewStore().Load());
    }

    [Fact]
    public void Saves_are_debounced_into_one_write()
    {
        var store = NewStore();
        store.Save([Timer("A", 1)], T0);
        store.Save([Timer("B", 2)], T0);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(TimerStore.SaveDelay, _scheduler.LastDelay);
        Assert.Equal(1, _scheduler.RunPending());
        Assert.Equal("B", Assert.Single(NewStore().Load()).Name);
    }

    [Fact]
    public void A_running_timer_comes_back_paused_and_counted_to_the_last_minute_saved()
    {
        var store = NewStore();
        var running = Timer("Studying", 5, runningSince: T0);
        store.Save([running], T0);
        store.SaveLastSeen(T0.AddMinutes(10));

        var loaded = Assert.Single(NewStore().Load());
        Assert.False(loaded.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(15), loaded.Accumulated);
    }

    [Fact]
    public void A_last_seen_before_the_start_counts_nothing()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(FilePath, $$"""
            { "Timers": [ { "Id": "{{id}}", "Name": "Studying", "Accumulated": "00:05:00", "RunningSince": "2026-11-25T09:30:00+00:00" } ],
              "LastSeen": "2026-11-25T09:10:00+00:00" }
            """);
        var loaded = Assert.Single(NewStore().Load());
        Assert.False(loaded.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), loaded.Accumulated);
    }

    [Fact]
    public void Two_running_timers_keep_the_latest_and_both_come_back_paused()
    {
        File.WriteAllText(FilePath, """
            { "Timers": [
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "Early", "Accumulated": "00:00:00", "RunningSince": "2026-11-25T09:00:00+00:00" },
                { "Id": "22222222-2222-2222-2222-222222222222", "Name": "Late", "Accumulated": "00:00:00", "RunningSince": "2026-11-25T09:20:00+00:00" } ],
              "LastSeen": "2026-11-25T09:30:00+00:00" }
            """);
        var loaded = NewStore().Load();
        Assert.All(loaded, t => Assert.False(t.IsRunning));
        // The early one stopped when the later one started; the later one counts to the last minute saved.
        Assert.Equal(TimeSpan.FromMinutes(20), loaded.Single(t => t.Name == "Early").Accumulated);
        Assert.Equal(TimeSpan.FromMinutes(10), loaded.Single(t => t.Name == "Late").Accumulated);
    }

    [Fact]
    public void A_corrupt_file_gives_no_timers_and_is_kept_as_timers_bad_json()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = NewStore();
        Assert.Empty(store.Load());
        Assert.True(File.Exists(store.BadFilePath));
        Assert.Equal("{ not json", File.ReadAllText(store.BadFilePath));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Damaged_entries_are_dropped_instead_of_stopping_Crest()
    {
        File.WriteAllText(FilePath, """
            { "Timers": [
                null,
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "  Reading  ", "Accumulated": "00:15:05" },
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "Same id again", "Accumulated": "00:01:00" },
                { "Id": "22222222-2222-2222-2222-222222222222", "Accumulated": "00:01:00" },
                { "Id": "33333333-3333-3333-3333-333333333333", "Name": "   ", "Accumulated": "00:01:00" },
                { "Name": "No id", "Accumulated": "00:01:00" },
                { "Id": "44444444-4444-4444-4444-444444444444", "Name": "Negative", "Accumulated": "-00:05:00" } ],
              "LastSeen": null }
            """);
        var loaded = NewStore().Load();
        Assert.Equal(["Reading", "Negative"], loaded.Select(t => t.Name));
        Assert.Equal(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(5), loaded[0].Accumulated);
        Assert.Equal(TimeSpan.Zero, loaded[1].Accumulated);
    }

    [Fact]
    public void A_file_with_no_timers_list_gives_no_timers()
    {
        File.WriteAllText(FilePath, """{ "LastSeen": null }""");
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void Flush_writes_a_pending_save_now()
    {
        var store = NewStore();
        store.Save([Timer("Gym", 3)], T0);
        store.Flush();
        Assert.Equal("Gym", Assert.Single(NewStore().Load()).Name);
        Assert.Equal(0, _scheduler.RunPending());
    }

    /// <summary>A copy of the scheduler in <see cref="SettingsStoreTests"/>: runs scheduled saves on demand.</summary>
    private sealed class FakeScheduler : IDebounceScheduler
    {
        private readonly List<Entry> _entries = [];

        public TimeSpan LastDelay { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            LastDelay = delay;
            var entry = new Entry(action);
            _entries.Add(entry);
            return entry;
        }

        /// <returns>How many scheduled actions ran.</returns>
        public int RunPending()
        {
            var due = _entries.Where(e => !e.Cancelled).ToList();
            _entries.Clear();
            foreach (var e in due) e.Action();
            return due.Count;
        }

        private sealed class Entry(Action action) : IDisposable
        {
            public Action Action { get; } = action;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
    }
}
