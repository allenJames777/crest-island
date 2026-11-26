using System.IO;
using Crest.Settings;

namespace Crest.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScheduler _scheduler = new();

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsStore NewStore() => new(SettingsPath, _scheduler);

    [Fact]
    public void Missing_file_gives_the_spec_defaults()
    {
        var s = NewStore().Load();
        Assert.Equal(ThemeName.Midnight, s.Theme);
        Assert.Equal(IslandShape.Notch, s.Shape);
        Assert.Equal(0, s.Transparency);
        Assert.Equal(50, s.HorizontalPosition);
        Assert.True(s.AutoTuckWhilePlaying);
        Assert.Equal(250, s.HoverDelayMs);
        Assert.Equal(600, s.CollapseDelayMs);
        Assert.Equal(AnimationMode.FollowWindows, s.Animations);
        Assert.True(s.IslandVisible);
        Assert.Null(s.DisplayDeviceName);
        Assert.Equal(WhileTimerRuns.TimerPill, s.WhileTimerRuns);
    }

    [Fact]
    public void An_unknown_while_timer_runs_value_falls_back_to_the_timer_pill()
    {
        Assert.Equal(WhileTimerRuns.TimerPill, (AppSettings.Defaults with { WhileTimerRuns = (WhileTimerRuns)7 }).Clamp().WhileTimerRuns);
        Assert.Equal(WhileTimerRuns.StayTucked, (AppSettings.Defaults with { WhileTimerRuns = WhileTimerRuns.StayTucked }).Clamp().WhileTimerRuns);
    }

    [Fact]
    public void Saved_settings_load_back_the_same()
    {
        var store = NewStore();
        store.Load();
        store.Update(s => s with { Theme = ThemeName.Frost, Transparency = 30, HorizontalPosition = 25, ExpandOn = ExpandOn.Click });
        _scheduler.RunPending();

        var loaded = NewStore().Load();
        Assert.Equal(store.Current, loaded);
        Assert.Equal(ThemeName.Frost, loaded.Theme);
        Assert.Equal(30, loaded.Transparency);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        File.WriteAllText(SettingsPath,
            """{ "Scale": 500, "HoverDelayMs": -5, "Transparency": 99, "HorizontalPosition": 150, "CornerRoundness": 10, "PeekSeconds": 0, "TopOffset": 90 }""");
        var s = NewStore().Load();
        Assert.Equal(120, s.Scale);
        Assert.Equal(0, s.HoverDelayMs);
        Assert.Equal(60, s.Transparency);
        Assert.Equal(100, s.HorizontalPosition);
        Assert.Equal(40, s.CornerRoundness);
        Assert.Equal(1, s.PeekSeconds);
        Assert.Equal(40, s.TopOffset);
    }

    [Fact]
    public void Invalid_colours_fall_back_to_defaults()
    {
        File.WriteAllText(SettingsPath, """{ "BackgroundColor": "red", "AccentColor": "#12345", "TextColor": "#abcdef" }""");
        var s = NewStore().Load();
        Assert.Equal("#000000", s.BackgroundColor);
        Assert.Equal("#7C5CFF", s.AccentColor);
        Assert.Equal("#ABCDEF", s.TextColor);
    }

    [Theory]
    [InlineData("#7c5cff", "#7C5CFF")]
    [InlineData("7C5CFF", "#7C5CFF")]
    [InlineData("#fff", "#FFFFFF")]
    [InlineData("0aB", "#00AABB")]
    [InlineData("  #0a0B0c ", "#0A0B0C")]
    [InlineData("#12", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Colours_are_normalised_and_typos_rejected(string? typed, string? expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeHex(typed));
    }

    [Fact]
    public void A_short_or_hashless_colour_in_the_file_is_accepted()
    {
        File.WriteAllText(SettingsPath, """{ "BackgroundColor": "123", "TextColor": "eeeeee" }""");
        var s = NewStore().Load();
        Assert.Equal("#112233", s.BackgroundColor);
        Assert.Equal("#EEEEEE", s.TextColor);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_kept_as_settings_bad_json()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = NewStore();
        var s = store.Load();
        Assert.Equal(AppSettings.Defaults, s);
        Assert.True(File.Exists(store.BadFilePath));
        Assert.Equal("{ this is not json", File.ReadAllText(store.BadFilePath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        File.WriteAllText(SettingsPath, """{ "SomethingNew": 1, "Theme": "Graphite" }""");
        Assert.Equal(ThemeName.Graphite, NewStore().Load().Theme);
    }

    [Fact]
    public void Update_raises_Changed_at_once_and_saves_once_after_quiet()
    {
        var store = NewStore();
        store.Load();
        var seen = new List<AppSettings>();
        store.Changed += seen.Add;

        store.Update(s => s with { Scale = 110 });
        store.Update(s => s with { Scale = 115 });

        Assert.Equal(2, seen.Count);
        Assert.Equal(115, seen[^1].Scale);
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(SettingsStore.SaveDelay, _scheduler.LastDelay);

        int saves = _scheduler.RunPending();
        Assert.Equal(1, saves);
        Assert.Equal(115, NewStore().Load().Scale);
    }

    [Fact]
    public void Update_clamps_the_changed_values()
    {
        var store = NewStore();
        store.Load();
        store.Update(s => s with { Transparency = 99, HorizontalPosition = -10 });
        Assert.Equal(60, store.Current.Transparency);
        Assert.Equal(0, store.Current.HorizontalPosition);
    }

    [Fact]
    public void Flush_writes_a_pending_change_now()
    {
        var store = NewStore();
        store.Load();
        store.Update(s => s with { Theme = ThemeName.AlbumTint });
        store.Flush();
        Assert.Equal(ThemeName.AlbumTint, NewStore().Load().Theme);
        Assert.Equal(0, _scheduler.RunPending());
    }

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
