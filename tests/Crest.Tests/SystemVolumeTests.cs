using Crest.Platform;

namespace Crest.Tests;

public class SystemVolumeTests
{
    [Theory]
    [InlineData(0.50, 120, 0.52)]   // one notch up: 2%
    [InlineData(0.50, -120, 0.48)]  // one notch down
    [InlineData(0.50, 360, 0.56)]   // three notches at once
    [InlineData(0.50, 60, 0.51)]    // half a notch (touchpads send small steps)
    [InlineData(0.99, 120, 1.00)]   // clamps at 100%
    [InlineData(0.01, -120, 0.00)]  // clamps at 0%
    public void Wheel_steps_change_the_volume_by_two_percent_a_notch(double level, int delta, double expected)
    {
        Assert.Equal(expected, VolumeMath.Step(level, delta), precision: 6);
    }

    [Theory]
    [InlineData(0.0, false, SpeakerLevel.Off)]
    [InlineData(0.30, true, SpeakerLevel.Muted)]
    [InlineData(0.20, false, SpeakerLevel.Low)]
    [InlineData(0.80, false, SpeakerLevel.High)]
    public void The_speaker_icon_follows_level_and_mute(double level, bool muted, SpeakerLevel expected)
    {
        Assert.Equal(expected, VolumeMath.Icon(level, muted));
    }
}
