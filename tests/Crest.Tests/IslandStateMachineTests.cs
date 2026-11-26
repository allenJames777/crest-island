using Crest.Island;
using Crest.Settings;
using S = Crest.Island.IslandState;

namespace Crest.Tests;

/// <summary>One test per rest-state rule and per row of the spec's transition table.</summary>
public class IslandStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double ms) => T0.AddMilliseconds(ms);

    private static readonly AppSettings Hover = AppSettings.Defaults;
    private static readonly AppSettings Click = AppSettings.Defaults with { ExpandOn = ExpandOn.Click };

    /// <summary>A machine with a playing session at T0.</summary>
    private static IslandStateMachine Playing(AppSettings settings)
    {
        var m = new IslandStateMachine(settings, T0);
        m.UpdateMedia(hasSession: true, isPlaying: true, T0);
        return m;
    }

    // ----- rest-state rules, in order -----

    [Fact]
    public void Nothing_playing_by_default_rests_tucked()
    {
        Assert.Equal(S.Tucked, new IslandStateMachine(Hover, T0).State);
    }

    [Theory]
    [InlineData(WhenNothingPlaying.Tuck, S.Tucked)]
    [InlineData(WhenNothingPlaying.KeepPill, S.Compact)]
    [InlineData(WhenNothingPlaying.Hide, S.Hidden)]
    public void When_nothing_plays_the_setting_picks_the_rest_state(WhenNothingPlaying when, S expected)
    {
        Assert.Equal(expected, new IslandStateMachine(Hover with { WhenNothingPlaying = when }, T0).State);
    }

    [Theory]
    [InlineData(true, S.Tucked)]
    [InlineData(false, S.Compact)]
    public void While_playing_auto_tuck_picks_the_rest_state(bool autoTuck, S expected)
    {
        Assert.Equal(expected, Playing(Hover with { AutoTuckWhilePlaying = autoTuck }).State);
    }

    [Fact]
    public void Tray_hide_wins_over_everything()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false });
        m.SetTrayHidden(true, At(10));
        Assert.Equal(S.Hidden, m.State);
        m.SetTrayHidden(false, At(20));
        Assert.Equal(S.Compact, m.State);
        Assert.Equal(S.Hidden, new IslandStateMachine(Hover with { IslandVisible = false }, T0).State);
    }

    [Theory]
    [InlineData(OverFullScreen.Hide, S.Hidden)]
    [InlineData(OverFullScreen.Tuck, S.Tucked)]
    [InlineData(OverFullScreen.Show, S.Compact)]
    public void Over_a_full_screen_app_the_setting_picks_the_rest_state(OverFullScreen over, S expected)
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, OverFullScreen = over });
        m.SetFullScreen(true, At(10));
        Assert.Equal(expected, m.State);
        m.SetFullScreen(false, At(20));
        Assert.Equal(S.Compact, m.State);
    }

    [Fact]
    public void A_pause_counts_as_playing_for_five_seconds()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, WhenNothingPlaying = WhenNothingPlaying.Hide });
        m.UpdateMedia(hasSession: true, isPlaying: false, At(1000));
        Assert.Equal(S.Compact, m.State);
        Assert.Equal(At(6000), m.NextWake);

        m.Tick(At(5999));
        Assert.Equal(S.Compact, m.State);
        m.Tick(At(6000));
        Assert.Equal(S.Hidden, m.State);
    }

    [Fact]
    public void Playing_again_within_the_grace_cancels_it()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, WhenNothingPlaying = WhenNothingPlaying.Hide });
        m.UpdateMedia(true, false, At(1000));
        m.UpdateMedia(true, true, At(3000));
        m.Tick(At(7000));
        Assert.Equal(S.Compact, m.State);
        Assert.Null(m.NextWake);
    }

    // ----- transition table -----

    [Fact]
    public void Hover_mode_tucked_opens_expanded_after_the_hover_delay()
    {
        var m = Playing(Hover);
        m.PointerEntered(At(0));
        m.Tick(At(249));
        Assert.Equal(S.Tucked, m.State);
        m.Tick(At(250));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Click_mode_tucked_opens_compact_after_the_hover_delay()
    {
        var m = Playing(Click);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        Assert.Equal(S.Compact, m.State);
    }

    [Fact]
    public void Hover_mode_compact_opens_expanded_after_the_hover_delay()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false });
        m.PointerEntered(At(0));
        m.Tick(At(250));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Click_mode_compact_stays_compact_on_hover()
    {
        var m = Playing(Click with { AutoTuckWhilePlaying = false });
        m.PointerEntered(At(0));
        m.Tick(At(1000));
        Assert.Equal(S.Compact, m.State);
        Assert.Null(m.NextWake);
    }

    [Theory]
    [InlineData(ExpandOn.Hover)]
    [InlineData(ExpandOn.Click)]
    public void Clicking_compact_opens_expanded(ExpandOn mode)
    {
        var m = Playing(Hover with { ExpandOn = mode, AutoTuckWhilePlaying = false });
        m.ClickedBackground(At(10));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Compact_that_is_not_the_rest_state_collapses_after_leaving()
    {
        var m = Playing(Click);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        Assert.Equal(S.Compact, m.State);
        m.PointerLeft(At(500));
        m.Tick(At(1099));
        Assert.Equal(S.Compact, m.State);
        m.Tick(At(1100));
        Assert.Equal(S.Tucked, m.State);
    }

    [Fact]
    public void Compact_rest_state_stays_after_leaving()
    {
        var m = Playing(Click with { AutoTuckWhilePlaying = false });
        m.PointerEntered(At(0));
        m.PointerLeft(At(100));
        Assert.Null(m.NextWake);
        Assert.Equal(S.Compact, m.State);
    }

    [Theory]
    [InlineData(ExpandOn.Hover)]
    [InlineData(ExpandOn.Click)]
    public void Expanded_collapses_to_rest_after_the_collapse_delay(ExpandOn mode)
    {
        var m = Playing(Hover with { ExpandOn = mode });
        m.ClickedBackground(At(0));
        m.PointerEntered(At(10));
        m.PointerLeft(At(1000));
        m.Tick(At(1599));
        Assert.Equal(S.Expanded, m.State);
        m.Tick(At(1600));
        Assert.Equal(S.Tucked, m.State);
    }

    [Fact]
    public void Hover_mode_click_on_expanded_background_keeps_it_open()
    {
        var m = Playing(Hover);
        m.ClickedBackground(At(0));
        m.ClickedBackground(At(100));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Click_mode_click_on_expanded_background_returns_to_rest()
    {
        var m = Playing(Click);
        m.ClickedBackground(At(0));
        m.ClickedBackground(At(100));
        Assert.Equal(S.Tucked, m.State);
    }

    [Theory]
    [InlineData(true, S.Tucked)]
    [InlineData(false, S.Compact)]
    public void A_track_change_peeks_then_returns_to_rest(bool autoTuck, S rest)
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = autoTuck });
        m.TrackChanged(At(0));
        Assert.Equal(S.Peek, m.State);
        m.Tick(At(2999));
        Assert.Equal(S.Peek, m.State);
        m.Tick(At(3000));
        Assert.Equal(rest, m.State);
    }

    [Fact]
    public void Peek_with_hover_mode_opens_expanded_on_hover()
    {
        var m = Playing(Hover);
        m.TrackChanged(At(0));
        m.PointerEntered(At(100));
        m.Tick(At(350));
        Assert.Equal(S.Expanded, m.State);
        m.Tick(At(5000));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Peek_with_click_mode_becomes_compact_on_hover()
    {
        var m = Playing(Click);
        m.TrackChanged(At(0));
        m.PointerEntered(At(100));
        m.Tick(At(350));
        Assert.Equal(S.Compact, m.State);
    }

    [Fact]
    public void Settings_hold_the_island_expanded_until_closed()
    {
        var m = Playing(Hover);
        m.SettingsOpened(At(0));
        Assert.Equal(S.Expanded, m.State);
        m.PointerEntered(At(100));
        m.PointerLeft(At(200));
        m.Tick(At(5000));
        Assert.Equal(S.Expanded, m.State);
        m.SetFullScreen(true, At(5100));
        Assert.Equal(S.Expanded, m.State);
        m.SettingsClosed(At(6000));
        Assert.Equal(S.Tucked, m.State);
    }

    [Fact]
    public void A_rest_input_change_moves_an_unhovered_island_to_the_new_rest()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, OverFullScreen = OverFullScreen.Hide });
        m.ClickedBackground(At(0));
        Assert.Equal(S.Expanded, m.State);
        m.SetFullScreen(true, At(100));
        Assert.Equal(S.Hidden, m.State);
    }

    [Fact]
    public void A_rest_input_change_waits_while_hovered_and_applies_on_leave()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, WhenNothingPlaying = WhenNothingPlaying.Hide });
        m.PointerEntered(At(0));
        m.UpdateMedia(hasSession: false, isPlaying: false, At(100));
        Assert.Equal(S.Compact, m.State);
        m.PointerLeft(At(200));
        m.Tick(At(800));
        Assert.Equal(S.Hidden, m.State);
    }

    [Fact]
    public void Re_entering_cancels_a_pending_collapse()
    {
        var m = Playing(Hover);
        m.ClickedBackground(At(0));
        m.PointerEntered(At(10));
        m.PointerLeft(At(100));
        m.PointerEntered(At(400));
        Assert.Null(m.NextWake);
        m.Tick(At(5000));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Peek_stays_while_the_pointer_rests_on_it()
    {
        var m = Playing(Hover);
        m.TrackChanged(At(0));
        m.PointerEntered(At(2900));
        m.Tick(At(3000));
        Assert.Equal(S.Peek, m.State);
        m.Tick(At(3150));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Leaving_a_peek_early_lets_the_peek_run_its_full_time()
    {
        var m = Playing(Hover with { HoverDelayMs = 1000 });
        m.TrackChanged(At(0));
        m.PointerEntered(At(100));
        m.PointerLeft(At(500));
        m.Tick(At(1100));
        Assert.Equal(S.Peek, m.State);
        m.Tick(At(3000));
        Assert.Equal(S.Tucked, m.State);
    }

    [Fact]
    public void Peek_never_fires_from_hidden()
    {
        var m = new IslandStateMachine(Hover with { IslandVisible = false }, T0);
        m.UpdateMedia(true, true, T0);
        m.TrackChanged(At(10));
        Assert.Equal(S.Hidden, m.State);
    }

    [Fact]
    public void Peek_never_fires_over_a_full_screen_app()
    {
        var m = Playing(Hover with { AutoTuckWhilePlaying = false, OverFullScreen = OverFullScreen.Show });
        m.SetFullScreen(true, At(0));
        m.TrackChanged(At(10));
        Assert.Equal(S.Compact, m.State);
    }

    [Fact]
    public void Peek_can_be_turned_off()
    {
        var m = Playing(Hover with { PeekOnTrackChange = false });
        m.TrackChanged(At(10));
        Assert.Equal(S.Tucked, m.State);
    }

    [Fact]
    public void Hidden_ignores_the_pointer()
    {
        var m = new IslandStateMachine(Hover with { WhenNothingPlaying = WhenNothingPlaying.Hide }, T0);
        m.PointerEntered(At(0));
        Assert.Null(m.NextWake);
        m.Tick(At(1000));
        Assert.Equal(S.Hidden, m.State);
    }

    [Fact]
    public void A_zero_hover_delay_opens_at_once()
    {
        var m = Playing(Hover with { HoverDelayMs = 0 });
        m.PointerEntered(At(0));
        Assert.Equal(S.Expanded, m.State);
    }
}

/// <summary>The timer rules added by the timers spec: rest order, opening page, peek and the typing hold.</summary>
public class IslandStateMachineTimerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double ms) => T0.AddMilliseconds(ms);

    private static readonly AppSettings Hover = AppSettings.Defaults;
    private static readonly AppSettings Click = AppSettings.Defaults with { ExpandOn = ExpandOn.Click };

    /// <summary>A machine with a timer running at T0, and music playing unless <paramref name="music"/> is false.</summary>
    private static IslandStateMachine Timing(AppSettings settings, bool music = true)
    {
        var m = new IslandStateMachine(settings, T0);
        if (music) m.UpdateMedia(hasSession: true, isPlaying: true, T0);
        m.SetTimerRunning(true, T0);
        return m;
    }

    // ----- rest order -----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_running_timer_rests_as_the_timer_pill(bool music)
    {
        Assert.Equal(S.TimerPill, Timing(Hover, music).State);
        Assert.Equal(S.TimerPill, Timing(Hover with { AutoTuckWhilePlaying = false }, music).State);
    }

    [Fact]
    public void Stay_tucked_keeps_the_music_rest_rules()
    {
        Assert.Equal(S.Tucked, Timing(Hover with { WhileTimerRuns = WhileTimerRuns.StayTucked }).State);
        Assert.Equal(S.Compact, Timing(Hover with { WhileTimerRuns = WhileTimerRuns.StayTucked, AutoTuckWhilePlaying = false }).State);
    }

    [Fact]
    public void The_timer_pill_shows_even_when_nothing_playing_means_hide()
    {
        Assert.Equal(S.TimerPill, Timing(Hover with { WhenNothingPlaying = WhenNothingPlaying.Hide }, music: false).State);
    }

    [Fact]
    public void Tray_hide_and_the_full_screen_rule_come_before_the_timer_pill()
    {
        var m = Timing(Hover);
        m.SetTrayHidden(true, At(10));
        Assert.Equal(S.Hidden, m.State);
        m.SetTrayHidden(false, At(20));
        Assert.Equal(S.TimerPill, m.State);

        m.SetFullScreen(true, At(30));
        Assert.Equal(S.Tucked, m.State);

        var shown = Timing(Hover with { OverFullScreen = OverFullScreen.Show });
        shown.SetFullScreen(true, At(30));
        Assert.Equal(S.TimerPill, shown.State);

        var hidden = Timing(Hover with { OverFullScreen = OverFullScreen.Hide });
        hidden.SetFullScreen(true, At(30));
        Assert.Equal(S.Hidden, hidden.State);
    }

    [Fact]
    public void Stopping_the_timer_goes_back_to_the_music_rest_state()
    {
        var m = Timing(Hover with { AutoTuckWhilePlaying = false });
        m.SetTimerRunning(false, At(10));
        Assert.Equal(S.Compact, m.State);
    }

    // ----- opening and the page -----

    [Fact]
    public void Hovering_the_timer_pill_opens_the_timers_page()
    {
        var m = Timing(Hover);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        Assert.Equal(S.Expanded, m.State);
        Assert.Equal(IslandPage.Timers, m.Page);
    }

    [Fact]
    public void In_click_mode_hovering_the_timer_pill_does_nothing_and_a_click_opens_timers()
    {
        var m = Timing(Click);
        m.PointerEntered(At(0));
        Assert.Null(m.NextWake);
        m.Tick(At(1000));
        Assert.Equal(S.TimerPill, m.State);

        m.ClickedBackground(At(1100));
        Assert.Equal(S.Expanded, m.State);
        Assert.Equal(IslandPage.Timers, m.Page);
    }

    [Fact]
    public void Clicking_the_timer_pill_in_hover_mode_opens_timers()
    {
        var m = Timing(Hover);
        m.ClickedBackground(At(0));
        Assert.Equal(S.Expanded, m.State);
        Assert.Equal(IslandPage.Timers, m.Page);
    }

    [Fact]
    public void A_song_peek_or_pill_opens_music_and_the_tucked_line_keeps_the_last_page()
    {
        // From the timer pill: Timers. Then a peek opens Music.
        var m = Timing(Hover);
        m.ClickedBackground(At(0));
        Assert.Equal(IslandPage.Timers, m.Page);
        m.PointerLeft(At(10));
        m.Tick(At(700));
        Assert.Equal(S.TimerPill, m.State);
        m.TrackChanged(At(1000));
        Assert.Equal(S.Peek, m.State);
        m.ClickedBackground(At(1100));
        Assert.Equal(IslandPage.Music, m.Page);

        // From the music pill: Music.
        var pill = new IslandStateMachine(Hover with { AutoTuckWhilePlaying = false }, T0);
        pill.UpdateMedia(true, true, T0);
        pill.ShowPage(IslandPage.Timers);
        pill.ClickedBackground(At(0));
        Assert.Equal(IslandPage.Music, pill.Page);

        // From the tucked line: the page used last.
        var tucked = new IslandStateMachine(Hover, T0);
        tucked.UpdateMedia(true, true, T0);
        tucked.ClickedBackground(At(0));
        tucked.ShowPage(IslandPage.Timers);
        tucked.PointerLeft(At(10));
        tucked.Tick(At(700));
        Assert.Equal(S.Tucked, tucked.State);
        tucked.ClickedBackground(At(800));
        Assert.Equal(IslandPage.Timers, tucked.Page);
    }

    [Fact]
    public void The_island_starts_on_the_music_page()
    {
        Assert.Equal(IslandPage.Music, new IslandStateMachine(Hover, T0).Page);
    }

    [Fact]
    public void A_song_change_peeks_over_the_timer_pill_and_returns_to_it()
    {
        var m = Timing(Hover);
        m.TrackChanged(At(0));
        Assert.Equal(S.Peek, m.State);
        m.Tick(At(3000));
        Assert.Equal(S.TimerPill, m.State);
    }

    // ----- the typing hold -----

    [Fact]
    public void While_typing_the_pointer_leaving_and_rest_changes_do_not_close_the_island()
    {
        var m = new IslandStateMachine(Hover, T0);
        m.UpdateMedia(true, true, T0);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        m.ShowPage(IslandPage.Timers);
        m.TypingStarted(At(300));
        Assert.True(m.IsHeld);

        m.PointerLeft(At(400));
        Assert.Null(m.NextWake);
        m.SetTimerRunning(true, At(500)); // Enter added and started a timer
        m.Tick(At(5000));
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void When_typing_ends_with_the_pointer_away_the_island_closes_after_the_delay()
    {
        var m = new IslandStateMachine(Hover, T0);
        m.UpdateMedia(true, true, T0);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        m.TypingStarted(At(300));
        m.PointerLeft(At(400));
        m.SetTimerRunning(true, At(500));

        m.TypingEnded(At(1000));
        Assert.False(m.IsHeld);
        Assert.Equal(At(1600), m.NextWake);
        m.Tick(At(1600));
        Assert.Equal(S.TimerPill, m.State);
    }

    [Fact]
    public void When_typing_ends_with_the_pointer_on_the_island_it_stays_open()
    {
        var m = new IslandStateMachine(Hover, T0);
        m.PointerEntered(At(0));
        m.Tick(At(250));
        m.TypingStarted(At(300));
        m.TypingEnded(At(1000));
        Assert.Null(m.NextWake);
        Assert.Equal(S.Expanded, m.State);
    }

    [Fact]
    public void Settings_still_holds_the_island_after_typing_ends()
    {
        var m = new IslandStateMachine(Hover, T0);
        m.SettingsOpened(At(0));
        m.TypingStarted(At(10));
        m.TypingEnded(At(20));
        Assert.True(m.IsHeld);
        m.SettingsClosed(At(30));
        Assert.False(m.IsHeld);
    }
}
