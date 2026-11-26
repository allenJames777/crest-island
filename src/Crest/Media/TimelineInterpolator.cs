namespace Crest.Media;

/// <summary>A track's timeline as last reported by the playing app.</summary>
public sealed record TimelineInfo(TimeSpan Position, TimeSpan Duration, DateTimeOffset LastUpdated, double Rate);

/// <summary>
/// Apps report the playback position only now and then. This estimates the current
/// position between reports, so the seek bar moves smoothly.
/// </summary>
public static class TimelineInterpolator
{
    /// <returns>The estimated position, or null when the app reports no usable timeline.</returns>
    public static TimeSpan? CurrentPosition(TimelineInfo? timeline, bool playing, DateTimeOffset now)
    {
        if (timeline is null || timeline.Duration <= TimeSpan.Zero) return null;

        var position = timeline.Position;
        if (playing)
        {
            var elapsed = now - timeline.LastUpdated;
            if (elapsed > TimeSpan.Zero)
                position += TimeSpan.FromTicks((long)(elapsed.Ticks * timeline.Rate));
        }

        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > timeline.Duration ? timeline.Duration : position;
    }
}
