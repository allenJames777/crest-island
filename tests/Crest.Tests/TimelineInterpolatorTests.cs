using Crest.Media;

namespace Crest.Tests;

public class TimelineInterpolatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static TimelineInfo Info(double posSec, double durSec, double rate = 1.0) =>
        new(TimeSpan.FromSeconds(posSec), TimeSpan.FromSeconds(durSec), T0, rate);

    [Fact]
    public void Playing_advances_by_elapsed_time()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(60, 200), playing: true, T0.AddSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(65), pos);
    }

    [Fact]
    public void Paused_stays_at_last_reported_position()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(60, 200), playing: false, T0.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(60), pos);
    }

    [Fact]
    public void Clamps_at_the_end_of_the_track()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(195, 200), playing: true, T0.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(200), pos);
    }

    [Fact]
    public void Never_goes_below_zero()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(-3, 200), playing: false, T0);
        Assert.Equal(TimeSpan.Zero, pos);
    }

    [Fact]
    public void Zero_duration_means_no_timeline()
    {
        Assert.Null(TimelineInterpolator.CurrentPosition(Info(10, 0), playing: true, T0.AddSeconds(1)));
        Assert.Null(TimelineInterpolator.CurrentPosition(null, playing: true, T0));
    }

    [Fact]
    public void Playback_rate_scales_the_elapsed_time()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(10, 200, rate: 2.0), playing: true, T0.AddSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(18), pos);
    }

    [Fact]
    public void A_clock_behind_the_last_update_does_not_rewind()
    {
        var pos = TimelineInterpolator.CurrentPosition(Info(60, 200), playing: true, T0.AddSeconds(-2));
        Assert.Equal(TimeSpan.FromSeconds(60), pos);
    }
}
