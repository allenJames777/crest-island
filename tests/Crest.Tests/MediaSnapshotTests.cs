using Crest.Media;

namespace Crest.Tests;

/// <summary>Which repeated media reports Crest may skip without missing a real change.</summary>
public class MediaSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static NowPlaying Snap(PlayStatus status, double positionSec, DateTimeOffset stamp, string title = "Song") =>
        new("spotify", "Spotify", title, "Artist", "", null, status,
            new TimelineInfo(TimeSpan.FromSeconds(positionSec), TimeSpan.FromSeconds(200), stamp, 1),
            new MediaCapabilities(true, true, true, true, false, false), null, null);

    [Fact]
    public void A_paused_app_restamping_the_same_position_is_skipped()
    {
        Assert.True(MediaService.SameForDisplay(Snap(PlayStatus.Paused, 78, T0), Snap(PlayStatus.Paused, 78, T0.AddSeconds(1))));
    }

    [Fact]
    public void A_playing_timeline_update_is_kept()
    {
        Assert.False(MediaService.SameForDisplay(Snap(PlayStatus.Playing, 78, T0), Snap(PlayStatus.Playing, 78, T0.AddSeconds(1))));
    }

    [Fact]
    public void A_seek_while_paused_is_kept()
    {
        Assert.False(MediaService.SameForDisplay(Snap(PlayStatus.Paused, 78, T0), Snap(PlayStatus.Paused, 120, T0.AddSeconds(1))));
    }

    [Fact]
    public void A_pause_or_play_is_kept()
    {
        Assert.False(MediaService.SameForDisplay(Snap(PlayStatus.Playing, 78, T0), Snap(PlayStatus.Paused, 78, T0.AddSeconds(1))));
        Assert.False(MediaService.SameForDisplay(Snap(PlayStatus.Paused, 78, T0), Snap(PlayStatus.Playing, 78, T0.AddSeconds(1))));
    }

    [Fact]
    public void A_new_song_is_kept()
    {
        Assert.False(MediaService.SameForDisplay(Snap(PlayStatus.Paused, 0, T0), Snap(PlayStatus.Paused, 0, T0, title: "Next")));
    }

    [Fact]
    public void An_identical_report_is_skipped()
    {
        Assert.True(MediaService.SameForDisplay(Snap(PlayStatus.Playing, 78, T0), Snap(PlayStatus.Playing, 78, T0)));
    }
}
