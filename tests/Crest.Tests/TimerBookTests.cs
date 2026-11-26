using Crest.Timers;

namespace Crest.Tests;

public class TimerBookTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    [Fact]
    public void Adding_a_timer_starts_it()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", T0)!;
        Assert.True(t.IsRunning);
        Assert.Equal(t.Id, book.Running!.Id);
        Assert.Equal(TimeSpan.FromMinutes(5), book.Elapsed(t.Id, At(5)));
    }

    [Fact]
    public void Starting_a_timer_pauses_the_running_one()
    {
        var book = new TimerBook();
        var study = book.Add("Studying", T0)!;
        var read = book.Add("Reading", At(10))!;

        Assert.Equal(read.Id, book.Running!.Id);
        Assert.Equal(TimeSpan.FromMinutes(10), book.Elapsed(study.Id, At(30)));

        book.Start(study.Id, At(20));
        Assert.Equal(study.Id, book.Running!.Id);
        Assert.Equal(TimeSpan.FromMinutes(10), book.Elapsed(read.Id, At(60)));
        Assert.Single(book.Timers, t => t.IsRunning);
    }

    [Fact]
    public void Pausing_keeps_the_total_and_resuming_adds_to_it()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", T0)!;
        book.Pause(t.Id, At(15));
        Assert.Null(book.Running);
        Assert.Equal(TimeSpan.FromMinutes(15), book.Elapsed(t.Id, At(40)));
        book.Start(t.Id, At(40));
        Assert.Equal(TimeSpan.FromMinutes(20), book.Elapsed(t.Id, At(45)));
    }

    [Fact]
    public void Resetting_a_running_timer_keeps_it_running_from_zero()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", T0)!;
        book.Reset(t.Id, At(30));
        Assert.True(book.Running!.Id == t.Id);
        Assert.Equal(TimeSpan.FromMinutes(2), book.Elapsed(t.Id, At(32)));
    }

    [Fact]
    public void Resetting_a_paused_timer_leaves_it_paused_at_zero()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", T0)!;
        book.Pause(t.Id, At(30));
        book.Reset(t.Id, At(31));
        Assert.Null(book.Running);
        Assert.Equal(TimeSpan.Zero, book.Elapsed(t.Id, At(50)));
    }

    [Fact]
    public void Deleting_the_running_timer_leaves_none_running_and_the_top_row_moves_to_the_last_timer()
    {
        var book = new TimerBook();
        var a = book.Add("A", T0)!;
        var b = book.Add("B", At(1))!;
        var c = book.Add("C", At(2))!;
        book.Start(a.Id, At(3));
        book.Delete(a.Id);
        Assert.Null(book.Running);
        Assert.Equal(c.Id, book.Top!.Id);
        Assert.Equal([b.Id], book.Others.Select(t => t.Id));
    }

    [Fact]
    public void Pause_all_counts_only_up_to_the_given_time()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", T0)!;
        // The PC went to sleep at minute 30; the pause runs after waking at minute 600.
        book.PauseAll(At(30));
        Assert.Null(book.Running);
        Assert.Equal(TimeSpan.FromMinutes(30), book.Elapsed(t.Id, At(600)));
    }

    [Fact]
    public void Pause_all_before_the_start_counts_nothing()
    {
        var book = new TimerBook();
        var t = book.Add("Studying", At(10))!;
        book.PauseAll(At(5));
        Assert.Equal(TimeSpan.Zero, book.Elapsed(t.Id, At(60)));
    }

    [Fact]
    public void The_top_row_is_the_running_timer_else_the_last_one_used_else_the_last_in_the_list()
    {
        var book = new TimerBook();
        Assert.Null(book.Top);

        var a = book.Add("A", T0)!;
        var b = book.Add("B", At(1))!;
        var c = book.Add("C", At(2))!;
        Assert.Equal(c.Id, book.Top!.Id);

        book.Start(a.Id, At(3));
        Assert.Equal(a.Id, book.Top!.Id);
        Assert.Equal([b.Id, c.Id], book.Others.Select(t => t.Id));

        book.Pause(a.Id, At(4));
        Assert.Equal(a.Id, book.Top!.Id);

        var restored = new TimerBook(book.Timers);
        Assert.Equal(c.Id, restored.Top!.Id);
        Assert.Equal([a.Id, b.Id], restored.Others.Select(t => t.Id));
    }

    [Fact]
    public void Names_are_trimmed_cut_to_40_characters_and_may_repeat()
    {
        var book = new TimerBook();
        Assert.Equal("Gym", book.Add("  Gym  ", T0)!.Name);
        Assert.Equal(new string('x', 40), book.Add(new string('x', 55), T0)!.Name);
        Assert.NotNull(book.Add("Gym", T0));
        Assert.Equal(2, book.Timers.Count(t => t.Name == "Gym"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_name_adds_nothing(string? name)
    {
        var book = new TimerBook();
        Assert.Null(book.Add(name, T0));
        Assert.Empty(book.Timers);
    }

    [Fact]
    public void Every_change_raises_Changed_once()
    {
        var book = new TimerBook();
        int changes = 0;
        book.Changed += () => changes++;
        var a = book.Add("A", T0)!;
        var b = book.Add("B", At(1))!;
        book.Start(a.Id, At(2));
        book.Pause(a.Id, At(3));
        book.Reset(b.Id, At(4));
        book.Delete(b.Id);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Loading_two_running_timers_keeps_only_the_latest_running()
    {
        var early = new ActivityTimer(Guid.NewGuid(), "Early", TimeSpan.FromMinutes(1), T0);
        var late = new ActivityTimer(Guid.NewGuid(), "Late", TimeSpan.FromMinutes(2), At(5));
        var book = new TimerBook([early, late]);
        Assert.Equal(late.Id, book.Running!.Id);
        Assert.False(book.Timers.Single(t => t.Id == early.Id).IsRunning);
    }
}
