using System.Text.RegularExpressions;

namespace Crest.Settings;

public enum ThemeName { Midnight, AlbumTint, Graphite, Frost, Custom }
public enum IslandShape { Notch, Floating }
public enum AccentMode { FromAlbumArt, Fixed }
public enum ExpandOn { Hover, Click }
public enum WhenNothingPlaying { Tuck, KeepPill, Hide }
public enum OverFullScreen { Tuck, Hide, Show }
public enum AnimationMode { FollowWindows, On, Off }
public enum WhileTimerRuns { TimerPill, StayTucked }

/// <summary>
/// Every Crest setting, with the spec's defaults (Data Model table). Saved as JSON;
/// unknown fields are ignored and missing fields take these defaults.
/// </summary>
public sealed partial record AppSettings
{
    // Look
    public ThemeName Theme { get; init; } = ThemeName.Midnight;
    public IslandShape Shape { get; init; } = IslandShape.Notch;
    public int Scale { get; init; } = 100;
    public int CornerRoundness { get; init; } = 100;
    public int Transparency { get; init; }
    public string BackgroundColor { get; init; } = "#000000";
    public string TextColor { get; init; } = "#F5F5F7";
    public AccentMode AccentMode { get; init; } = AccentMode.FromAlbumArt;
    public string AccentColor { get; init; } = "#7C5CFF";
    public bool ShowAlbumArt { get; init; } = true;
    public bool ShowEqualizer { get; init; } = true;

    // Behaviour
    public ExpandOn ExpandOn { get; init; } = ExpandOn.Hover;
    public int HoverDelayMs { get; init; } = 250;
    public int CollapseDelayMs { get; init; } = 600;
    public WhenNothingPlaying WhenNothingPlaying { get; init; } = WhenNothingPlaying.Tuck;
    public bool AutoTuckWhilePlaying { get; init; } = true;
    public bool PeekOnTrackChange { get; init; } = true;
    public int PeekSeconds { get; init; } = 3;
    public OverFullScreen OverFullScreen { get; init; } = OverFullScreen.Tuck;
    public WhileTimerRuns WhileTimerRuns { get; init; } = WhileTimerRuns.TimerPill;

    // Display
    public string? DisplayDeviceName { get; init; }
    public int TopOffset { get; init; }
    public int HorizontalPosition { get; init; } = 50;

    // System
    public bool StartWithWindows { get; init; }
    public AnimationMode Animations { get; init; } = AnimationMode.FollowWindows;
    public bool IslandVisible { get; init; } = true;

    public static AppSettings Defaults { get; } = new();

    /// <summary>Returns a copy with every range and colour brought back into its allowed values.</summary>
    public AppSettings Clamp() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : Defaults.Theme,
        Shape = Enum.IsDefined(Shape) ? Shape : Defaults.Shape,
        Scale = Math.Clamp(Scale, 85, 120),
        CornerRoundness = Math.Clamp(CornerRoundness, 40, 100),
        Transparency = Math.Clamp(Transparency, 0, 60),
        BackgroundColor = ValidHex(BackgroundColor, Defaults.BackgroundColor),
        TextColor = ValidHex(TextColor, Defaults.TextColor),
        AccentMode = Enum.IsDefined(AccentMode) ? AccentMode : Defaults.AccentMode,
        AccentColor = ValidHex(AccentColor, Defaults.AccentColor),
        ExpandOn = Enum.IsDefined(ExpandOn) ? ExpandOn : Defaults.ExpandOn,
        HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 1500),
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 0, 3000),
        WhenNothingPlaying = Enum.IsDefined(WhenNothingPlaying) ? WhenNothingPlaying : Defaults.WhenNothingPlaying,
        PeekSeconds = Math.Clamp(PeekSeconds, 1, 10),
        OverFullScreen = Enum.IsDefined(OverFullScreen) ? OverFullScreen : Defaults.OverFullScreen,
        WhileTimerRuns = Enum.IsDefined(WhileTimerRuns) ? WhileTimerRuns : Defaults.WhileTimerRuns,
        DisplayDeviceName = string.IsNullOrWhiteSpace(DisplayDeviceName) ? null : DisplayDeviceName,
        TopOffset = Math.Clamp(TopOffset, 0, 40),
        HorizontalPosition = Math.Clamp(HorizontalPosition, 0, 100),
        Animations = Enum.IsDefined(Animations) ? Animations : Defaults.Animations,
    };

    private static string ValidHex(string? value, string fallback) => NormalizeHex(value) ?? fallback;

    /// <summary>"#RRGGBB" in capitals from "#RRGGBB", "RRGGBB", "#RGB" or "RGB" (spaces ignored); null when invalid.</summary>
    public static string? NormalizeHex(string? value)
    {
        if (value is null) return null;
        string v = value.Trim().TrimStart('#');
        if (v.Length == 3 && ShortHex().IsMatch(v)) v = string.Concat(v.Select(c => new string(c, 2)));
        return v.Length == 6 && LongHex().IsMatch(v) ? "#" + v.ToUpperInvariant() : null;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{6}$")]
    private static partial Regex LongHex();

    [GeneratedRegex("^[0-9A-Fa-f]{3}$")]
    private static partial Regex ShortHex();
}
