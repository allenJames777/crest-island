using Crest.Island;
using Crest.Settings;

namespace Crest.Tests;

public class ThemePaletteTests
{
    private static readonly AppSettings Defaults = AppSettings.Defaults;

    private static Rgba Hex(string hex) => Rgba.FromHex(hex);

    private static byte Alpha(double opacity) => (byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero);

    [Fact]
    public void Midnight_matches_the_design_page()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.Midnight }, artHue: null);
        Assert.Equal(Hex("#000000"), c.Background);
        Assert.Equal(Hex("#F5F5F7"), c.Foreground);
        Assert.Equal(Hex("#F5F5F7") with { A = Alpha(0.58) }, c.Muted);
        Assert.Equal(new Rgba(255, 255, 255, Alpha(0.08)), c.Line);
        Assert.Equal(new Rgba(255, 255, 255, Alpha(0.16)), c.Track);
        Assert.Equal(new Rgba(255, 255, 255, Alpha(0.09)), c.Hover);
        Assert.Equal(Hex("#F5F5F7"), c.Button);
        Assert.Equal(Hex("#000000"), c.ButtonForeground);
        Assert.False(c.IsLight);
        Assert.Equal(1.0, c.Glow);
    }

    [Fact]
    public void Graphite_matches_the_design_page()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.Graphite }, artHue: null);
        Assert.Equal(Hex("#202127"), c.Background);
        Assert.Equal(Hex("#EEEFF3"), c.Foreground);
        Assert.Equal(new Rgba(255, 255, 255, Alpha(0.10)), c.Line);
        Assert.Equal(Hex("#202127"), c.ButtonForeground);
    }

    [Fact]
    public void Frost_is_light_with_black_washes_and_a_softer_glow()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.Frost }, artHue: 200);
        Assert.Equal(Hex("#F7F7FA"), c.Background);
        Assert.Equal(Hex("#121318"), c.Foreground);
        Assert.Equal(new Rgba(0, 0, 0, Alpha(0.12)), c.Track);
        Assert.True(c.IsLight);
        Assert.Equal(0.5, c.Glow);
        Assert.Equal(Rgba.FromHsl(200, 0.72, 0.48), c.Accent);
        Assert.Equal(Rgba.FromHsl(230, 0.78, 0.56), c.Accent2);
    }

    [Fact]
    public void Album_tint_colours_the_background_with_the_track_hue()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.AlbumTint }, artHue: 168);
        Assert.Equal(Rgba.FromHsl(168, 0.38, 0.10), c.Background);
        Assert.Equal(Rgba.FromHsl(168, 0.40, 0.12), c.ButtonForeground);
        Assert.Equal(new Rgba(255, 255, 255, Alpha(0.18)), c.Track);
    }

    [Theory]
    [InlineData(ThemeName.Midnight)]
    [InlineData(ThemeName.AlbumTint)]
    [InlineData(ThemeName.Graphite)]
    [InlineData(ThemeName.Frost)]
    [InlineData(ThemeName.Custom)]
    public void Transparency_sets_the_background_alpha_in_every_theme(ThemeName theme)
    {
        byte AlphaAt(int t) => ThemePalette.For(Defaults with { Theme = theme, Transparency = t }, 120).Background.A;
        Assert.Equal(255, AlphaAt(0));
        Assert.Equal(179, AlphaAt(30));
        Assert.Equal(102, AlphaAt(60));
        // Text stays solid.
        Assert.Equal(255, ThemePalette.For(Defaults with { Theme = theme, Transparency = 60 }, 120).Foreground.A);
    }

    [Fact]
    public void Accent_follows_the_album_art_hue_on_dark_themes()
    {
        var c = ThemePalette.For(Defaults, artHue: 300);
        Assert.Equal(Rgba.FromHsl(300, 0.92, 0.70), c.Accent);
        Assert.Equal(Rgba.FromHsl(330, 0.95, 0.76), c.Accent2);
    }

    [Fact]
    public void Without_art_the_accent_uses_the_accent_colour_hue()
    {
        double hue = Hex("#7C5CFF").ToHsl().Hue;
        var c = ThemePalette.For(Defaults, artHue: null);
        Assert.Equal(Rgba.FromHsl(hue, 0.92, 0.70), c.Accent);
    }

    [Fact]
    public void Fixed_accent_ignores_the_art()
    {
        var c = ThemePalette.For(Defaults with { AccentMode = AccentMode.Fixed, AccentColor = "#FF8800" }, artHue: 200);
        Assert.Equal(Hex("#FF8800"), c.Accent);
        Assert.NotEqual(Rgba.FromHsl(200, 0.92, 0.70), c.Accent);
    }

    [Fact]
    public void Custom_dark_background_derives_washes_from_the_text_colour()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.Custom, BackgroundColor = "#102030", TextColor = "#E0E0E0" }, null);
        Assert.Equal(Hex("#102030"), c.Background);
        Assert.Equal(Hex("#E0E0E0"), c.Foreground);
        Assert.Equal(Hex("#E0E0E0") with { A = Alpha(0.58) }, c.Muted);
        Assert.Equal(Hex("#E0E0E0") with { A = Alpha(0.16) }, c.Track);
        Assert.Equal(Hex("#E0E0E0"), c.Button);
        Assert.Equal(Hex("#102030"), c.ButtonForeground);
        Assert.False(c.IsLight);
    }

    [Fact]
    public void Custom_light_background_counts_as_light()
    {
        var c = ThemePalette.For(Defaults with { Theme = ThemeName.Custom, BackgroundColor = "#F0E8D8", TextColor = "#202020" }, 40);
        Assert.True(c.IsLight);
        Assert.Equal(Rgba.FromHsl(40, 0.72, 0.48), c.Accent);
    }

    // ----- album-art colour pick: the most vivid colour that covers a fair part of the art -----

    [Fact]
    public void Single_colour_art_gives_its_hue()
    {
        AssertHue(0, ArtHue(((255, 0, 0), 256)));
        AssertHue(240, ArtHue(((0, 0, 255), 256)));
    }

    [Fact]
    public void Two_colour_art_picks_the_bigger_real_colour_not_a_mix()
    {
        // The old average turned red and blue into purple (300), a colour not in the art.
        AssertHue(0, ArtHue(((255, 0, 0), 154), ((0, 0, 255), 102)));
    }

    [Fact]
    public void Complementary_colours_still_give_a_hue()
    {
        // Green and magenta average to grey; the pick keeps the bigger one.
        AssertHue(120, ArtHue(((0, 200, 0), 141), ((200, 0, 200), 115)));
    }

    [Fact]
    public void A_small_vivid_area_beats_a_large_grey_one()
    {
        AssertHue(new Rgba(30, 60, 220).ToHsl().Hue, ArtHue(((220, 220, 220), 218), ((30, 60, 220), 38)));
    }

    [Fact]
    public void A_vivid_accent_beats_a_muted_background()
    {
        AssertHue(new Rgba(0, 200, 180).ToHsl().Hue, ArtHue(((60, 50, 45), 230), ((0, 200, 180), 26)));
    }

    [Fact]
    public void A_colour_split_across_two_hue_bands_still_wins()
    {
        // Red-orange at 8 and 12 degrees sits on a band edge; together it outweighs the larger single blue area.
        AssertHue(10, ArtHue(((255, 34, 0), 70), ((255, 51, 0), 70), ((0, 0, 255), 90), ((128, 128, 128), 26)));
    }

    [Fact]
    public void A_tiny_speck_of_colour_is_ignored()
    {
        Assert.Null(ArtHue(((128, 128, 128), 252), ((255, 0, 0), 4)));
    }

    [Fact]
    public void Muted_art_falls_back_to_its_average_hue()
    {
        AssertHue(new Rgba(130, 110, 100).ToHsl().Hue, ArtHue(((130, 110, 100), 256)));
    }

    [Fact]
    public void Grey_or_empty_art_has_no_hue()
    {
        Assert.Null(ArtHue(((128, 128, 128), 256)));
        Assert.Null(ThemePalette.ArtHue([]));
    }

    private static double? ArtHue(params ((byte R, byte G, byte B) Rgb, int Count)[] parts) =>
        ThemePalette.ArtHue(parts.SelectMany(p => Pixels(p.Rgb, p.Count)).ToArray());

    private static void AssertHue(double expected, double? actual)
    {
        Assert.NotNull(actual);
        double d = Math.Abs(expected - actual!.Value) % 360;
        Assert.True(Math.Min(d, 360 - d) <= 1.0, $"expected hue {expected:F1}, got {actual:F1}");
    }

    /// <summary>BGRA bytes for n opaque pixels of one RGB colour.</summary>
    private static byte[] Pixels((byte R, byte G, byte B) rgb, int n) =>
        Enumerable.Repeat(new[] { rgb.B, rgb.G, rgb.R, (byte)255 }, n).SelectMany(p => p).ToArray();
}
