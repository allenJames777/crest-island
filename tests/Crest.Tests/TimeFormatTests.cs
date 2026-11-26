using Crest.Timers;

namespace Crest.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(59 * 60 + 59, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3600 + 5 * 60 + 7, "1:05:07")]
    [InlineData(100 * 3600, "100:00:00")]
    [InlineData(-5, "0:00")]
    public void The_open_island_shows_minutes_and_seconds_then_hours(int seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(42 * 60 + 59, "42m")]
    [InlineData(59 * 60 + 59, "59m")]
    [InlineData(3600, "1h 00m")]
    [InlineData(3600 + 5 * 60, "1h 05m")]
    [InlineData(100 * 3600, "100h 00m")]
    public void The_pill_shows_whole_minutes_only(int seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Minutes(TimeSpan.FromSeconds(seconds)));
    }
}
