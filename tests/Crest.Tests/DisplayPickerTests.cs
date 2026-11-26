using Crest.Platform;

namespace Crest.Tests;

public class DisplayPickerTests
{
    private static readonly DisplayCandidate Main = new(@"\\.\DISPLAY1", IsPrimary: true);
    private static readonly DisplayCandidate Side = new(@"\\.\DISPLAY2", IsPrimary: false);

    [Fact]
    public void No_saved_display_uses_the_main_display()
    {
        Assert.Equal(1, DisplayPicker.Pick([Side, Main], saved: null));
    }

    [Fact]
    public void A_saved_display_that_is_present_is_used()
    {
        Assert.Equal(0, DisplayPicker.Pick([Side, Main], saved: @"\\.\DISPLAY2"));
        Assert.Equal(0, DisplayPicker.Pick([Side, Main], saved: @"\\.\display2"));
    }

    [Fact]
    public void A_saved_display_that_is_missing_falls_back_to_main()
    {
        Assert.Equal(0, DisplayPicker.Pick([Main], saved: @"\\.\DISPLAY2"));
        Assert.Equal(0, DisplayPicker.Pick([Main], saved: "not a display"));
    }

    [Fact]
    public void Without_a_main_display_flag_the_first_is_used()
    {
        Assert.Equal(0, DisplayPicker.Pick([Side with { IsPrimary = false }, new(@"\\.\DISPLAY3", false)], saved: null));
    }

    [Fact]
    public void No_displays_gives_no_pick()
    {
        Assert.Equal(-1, DisplayPicker.Pick([], saved: null));
    }

    [Theory]
    [InlineData(50, 1508)]   // centre of a 3440 px display for a 424 px window
    [InlineData(0, 0)]       // left edge
    [InlineData(100, 3016)]  // right edge, window still fully on screen
    [InlineData(25, 754)]
    public void Horizontal_position_slides_the_window_along_the_top(int percent, int expectedLeft)
    {
        Assert.Equal(expectedLeft, DisplayPicker.WindowLeft(displayLeft: 0, displayWidth: 3440, windowWidth: 424, percent));
    }

    [Fact]
    public void Horizontal_position_works_on_a_display_to_the_right()
    {
        Assert.Equal(3440 + 1508, DisplayPicker.WindowLeft(displayLeft: 3440, displayWidth: 3440, windowWidth: 424, 50));
    }

    [Fact]
    public void Out_of_range_positions_stay_on_screen()
    {
        Assert.Equal(0, DisplayPicker.WindowLeft(0, 3440, 424, -20));
        Assert.Equal(3016, DisplayPicker.WindowLeft(0, 3440, 424, 140));
    }
}
