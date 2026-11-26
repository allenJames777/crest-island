using Crest.Media;

namespace Crest.Tests;

public class SessionPickerTests
{
    private static SessionInfo S(string id, PlayStatus status) => new(id, status);

    [Fact]
    public void No_sessions_picks_nothing()
    {
        Assert.Null(new SessionPicker().Pick([], windowsCurrentId: null));
    }

    [Fact]
    public void Windows_current_session_wins_when_it_is_playing()
    {
        var sessions = new[] { S("spotify", PlayStatus.Playing), S("edge", PlayStatus.Playing) };
        Assert.Equal("edge", new SessionPicker().Pick(sessions, "edge"));
    }

    [Fact]
    public void A_playing_app_beats_a_paused_windows_current()
    {
        var sessions = new[] { S("edge", PlayStatus.Paused), S("spotify", PlayStatus.Playing) };
        Assert.Equal("spotify", new SessionPicker().Pick(sessions, "edge"));
    }

    [Fact]
    public void With_nothing_playing_the_windows_current_session_is_shown()
    {
        var sessions = new[] { S("spotify", PlayStatus.Paused), S("edge", PlayStatus.Paused) };
        Assert.Equal("edge", new SessionPicker().Pick(sessions, "edge"));
    }

    [Fact]
    public void With_no_windows_current_and_nothing_playing_the_first_session_is_shown()
    {
        var sessions = new[] { S("spotify", PlayStatus.Paused), S("edge", PlayStatus.Stopped) };
        Assert.Equal("spotify", new SessionPicker().Pick(sessions, null));
    }

    [Fact]
    public void Next_moves_to_the_following_session_and_wraps()
    {
        var sessions = new[] { S("a", PlayStatus.Playing), S("b", PlayStatus.Paused), S("c", PlayStatus.Paused) };
        var picker = new SessionPicker();
        Assert.Equal("b", picker.Next(sessions, shownId: "a"));
        Assert.Equal("c", picker.Next(sessions, shownId: "b"));
        Assert.Equal("a", picker.Next(sessions, shownId: "c"));
    }

    [Fact]
    public void A_switched_choice_is_kept_even_when_another_app_starts_playing()
    {
        var picker = new SessionPicker();
        var sessions = new[] { S("spotify", PlayStatus.Playing), S("edge", PlayStatus.Paused) };
        picker.Next(sessions, shownId: "spotify");
        Assert.Equal("edge", picker.Pick(sessions, "spotify"));
        Assert.Equal("edge", picker.Pick([S("spotify", PlayStatus.Playing), S("edge", PlayStatus.Paused)], "spotify"));
        Assert.Equal("edge", picker.Pinned);
    }

    [Fact]
    public void A_choice_is_dropped_when_its_app_closes()
    {
        var picker = new SessionPicker();
        picker.Next([S("spotify", PlayStatus.Playing), S("edge", PlayStatus.Paused)], shownId: "spotify");
        Assert.Equal("spotify", picker.Pick([S("spotify", PlayStatus.Playing)], "spotify"));
        Assert.Null(picker.Pinned);
        // A later return of that app does not bring the old choice back.
        Assert.Equal("spotify", picker.Pick([S("spotify", PlayStatus.Playing), S("edge", PlayStatus.Paused)], "spotify"));
    }

    [Fact]
    public void Next_with_one_session_keeps_it()
    {
        var picker = new SessionPicker();
        Assert.Equal("spotify", picker.Next([S("spotify", PlayStatus.Playing)], shownId: "spotify"));
    }

    [Fact]
    public void Next_from_an_unknown_shown_session_starts_at_the_first()
    {
        var picker = new SessionPicker();
        Assert.Equal("a", picker.Next([S("a", PlayStatus.Paused), S("b", PlayStatus.Paused)], shownId: "gone"));
    }
}
