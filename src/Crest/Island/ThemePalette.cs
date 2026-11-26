using System.Globalization;
using Crest.Settings;

namespace Crest.Island;

/// <summary>A colour with alpha, kept free of WPF types so the palette can be unit-tested.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba FromHex(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    /// <param name="hue">Degrees; wraps around.</param>
    /// <param name="saturation">0 to 1.</param>
    /// <param name="lightness">0 to 1.</param>
    public static Rgba FromHsl(double hue, double saturation, double lightness)
    {
        double h = ((hue % 360) + 360) % 360 / 60.0;
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = lightness - c / 2;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgba(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>Hue in degrees, saturation and lightness 0 to 1.</summary>
    public (double Hue, double Saturation, double Lightness) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s, l);
    }

    public Rgba WithAlpha(double opacity) => this with { A = ToByte(opacity) };

    /// <summary>Relative luminance (WCAG), 0 for black to 1 for white.</summary>
    public double Luminance
    {
        get
        {
            static double Lin(byte v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    private static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255, MidpointRounding.AwayFromZero), 0, 255);
}

/// <summary>Every colour the island draws with, for one theme and track.</summary>
public sealed record IslandColors(
    Rgba Background, Rgba Foreground, Rgba Muted, Rgba Line, Rgba Track, Rgba Hover,
    Rgba Button, Rgba ButtonForeground, Rgba Accent, Rgba Accent2, double Glow, bool IsLight);

/// <summary>
/// The island's colours. Preset values come from the locked design page (its PRESETS);
/// the accent follows the album art's hue unless a fixed accent is chosen.
/// </summary>
public static class ThemePalette
{
    /// <param name="artHue">Hue of the current album art, or null when there is no usable art.</param>
    public static IslandColors For(AppSettings settings, double? artHue)
    {
        double fallbackHue = Rgba.FromHex(settings.AccentColor).ToHsl().Hue;
        double trackHue = artHue ?? fallbackHue;

        var colors = settings.Theme switch
        {
            ThemeName.AlbumTint => Preset(Rgba.FromHsl(trackHue, 0.38, 0.10), Hex("#F6F4FA"), 0.62, 0.09, 0.18, 0.10,
                Hex("#FFFFFF"), Rgba.FromHsl(trackHue, 0.40, 0.12), light: false),
            ThemeName.Graphite => Preset(Hex("#202127"), Hex("#EEEFF3"), 0.58, 0.10, 0.16, 0.09,
                Hex("#EEEFF3"), Hex("#202127"), light: false),
            ThemeName.Frost => Preset(Hex("#F7F7FA"), Hex("#121318"), 0.56, 0.09, 0.12, 0.06,
                Hex("#121318"), Hex("#F7F7FA"), light: true),
            ThemeName.Custom => Custom(settings),
            _ => Preset(Hex("#000000"), Hex("#F5F5F7"), 0.58, 0.08, 0.16, 0.09,
                Hex("#F5F5F7"), Hex("#000000"), light: false),
        };

        // Lines, tracks and hovers are white washes on dark themes and black washes on light ones.
        Rgba accent, accent2;
        if (settings.AccentMode == AccentMode.Fixed)
        {
            accent = Hex(settings.AccentColor);
            var (h, s, l) = accent.ToHsl();
            accent2 = Rgba.FromHsl(h + 30, s, l);
        }
        else
        {
            double hue = artHue ?? fallbackHue;
            accent = colors.IsLight ? Rgba.FromHsl(hue, 0.72, 0.48) : Rgba.FromHsl(hue, 0.92, 0.70);
            accent2 = colors.IsLight ? Rgba.FromHsl(hue + 30, 0.78, 0.56) : Rgba.FromHsl(hue + 30, 0.95, 0.76);
        }

        double opacity = (100 - settings.Transparency) / 100.0;
        return colors with
        {
            Background = colors.Background.WithAlpha(opacity),
            Accent = accent,
            Accent2 = accent2,
            Glow = colors.IsLight ? 0.5 : 1.0,
        };
    }

    /// <summary>
    /// The album art's colour for the accent: the hue of the most vivid colour that covers a fair part of the art
    /// (at least 3% of it), so a red and blue cover gives red or blue rather than a purple that is not in the art.
    /// Art with no vivid area falls back to its average colour; grey or empty art gives null.
    /// </summary>
    public static double? ArtHue(ReadOnlySpan<byte> bgra)
    {
        const int Bins = 36;             // 10 degrees each
        const double MinSaturation = 0.25, MinBrightness = 0.2, MinCoverage = 0.03;

        Span<double> weight = stackalloc double[Bins];
        var vivid = new List<(double Hue, double Weight)>();
        int opaque = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            opaque++;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double brightness = max / 255.0;
            double saturation = max == 0 ? 0 : (max - min) / (double)max;
            if (saturation < MinSaturation || brightness < MinBrightness) continue;
            double hue = new Rgba(r, g, b).ToHsl().Hue;
            double w = saturation * brightness;
            weight[(int)(hue / 10) % Bins] += w;
            vivid.Add((hue, w));
        }
        if (opaque == 0) return null;
        if (vivid.Count < opaque * MinCoverage) return AverageHue(bgra);

        // The strongest hue band, counting half of each neighbouring band so a colour on a band edge is not split.
        int best = 0;
        double bestScore = -1;
        for (int k = 0; k < Bins; k++)
        {
            double score = weight[k] + 0.5 * (weight[(k + Bins - 1) % Bins] + weight[(k + 1) % Bins]);
            if (score > bestScore) { bestScore = score; best = k; }
        }

        // Weighted circular mean of the hues in that band and its two neighbours.
        double x = 0, y = 0;
        foreach (var (hue, w) in vivid)
        {
            int bin = (int)(hue / 10) % Bins;
            int distance = Math.Min((bin - best + Bins) % Bins, (best - bin + Bins) % Bins);
            if (distance > 1) continue;
            x += w * Math.Cos(hue * Math.PI / 180);
            y += w * Math.Sin(hue * Math.PI / 180);
        }
        double mean = Math.Atan2(y, x) * 180 / Math.PI;
        return mean < 0 ? mean + 360 : mean;
    }

    /// <summary>Hue of the average colour of BGRA pixels, or null when the average is too grey to have a useful hue.</summary>
    private static double? AverageHue(ReadOnlySpan<byte> bgra)
    {
        long b = 0, g = 0, r = 0, n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
            n++;
        }
        if (n == 0) return null;
        var average = new Rgba((byte)(r / n), (byte)(g / n), (byte)(b / n));
        var (hue, saturation, _) = average.ToHsl();
        return saturation < 0.08 ? null : hue;
    }

    private static IslandColors Custom(AppSettings s)
    {
        var bg = Hex(s.BackgroundColor);
        var fg = Hex(s.TextColor);
        return Preset(bg, fg, 0.58, 0.08, 0.16, 0.09, fg, bg, light: bg.Luminance > 0.5, washFromForeground: true);
    }

    private static IslandColors Preset(Rgba bg, Rgba fg, double muted, double line, double track, double hover,
        Rgba button, Rgba buttonFg, bool light, bool washFromForeground = false)
    {
        var wash = washFromForeground ? fg : light ? new Rgba(0, 0, 0) : new Rgba(255, 255, 255);
        return new IslandColors(bg, fg, fg.WithAlpha(muted), wash.WithAlpha(line), wash.WithAlpha(track),
            wash.WithAlpha(hover), button, buttonFg, default, default, 0, light);
    }

    private static Rgba Hex(string hex) => Rgba.FromHex(hex);
}
