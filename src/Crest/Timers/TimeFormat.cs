namespace Crest.Timers;

/// <summary>How timer totals are written (spec Data Model).</summary>
public static class TimeFormat
{
    /// <summary>In the open island: <c>m:ss</c> under an hour, <c>h:mm:ss</c> from an hour up.</summary>
    public static string Clock(TimeSpan t)
    {
        long s = Math.Max(0, (long)t.TotalSeconds);
        long h = s / 3600, m = s % 3600 / 60, sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    /// <summary>In the resting timer pill, whole minutes only: <c>&lt;1m</c>, <c>42m</c>, <c>1h 05m</c>.</summary>
    public static string Minutes(TimeSpan t)
    {
        long m = Math.Max(0, (long)t.TotalMinutes);
        if (m < 1) return "<1m";
        long h = m / 60;
        return h > 0 ? $"{h}h {m % 60:00}m" : $"{m}m";
    }
}
