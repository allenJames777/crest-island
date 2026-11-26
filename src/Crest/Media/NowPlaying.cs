using System.Windows.Media.Imaging;

namespace Crest.Media;

public enum PlayStatus { None, Stopped, Paused, Playing }

public enum RepeatMode { None, Track, List }

/// <summary>What the current app lets us do, from its media controls.</summary>
public sealed record MediaCapabilities(bool PlayPause, bool Next, bool Previous, bool Seek, bool Shuffle, bool Repeat)
{
    public static readonly MediaCapabilities None = new(false, false, false, false, false, false);
}

/// <summary>An immutable snapshot of what is playing.</summary>
public sealed record NowPlaying(
    string SourceAppId,
    string SourceAppName,
    string Title,
    string Artist,
    string AlbumTitle,
    BitmapSource? Art,
    PlayStatus Status,
    TimelineInfo? Timeline,
    MediaCapabilities Can,
    bool? ShuffleOn,
    RepeatMode? Repeat,
    int SessionIndex = 0,
    int SessionCount = 0)
{
    public static readonly NowPlaying Empty =
        new("", "", "", "", "", null, PlayStatus.None, null, MediaCapabilities.None, null, null);

    /// <summary>True when an app has a media session open, playing or not.</summary>
    public bool HasSession => Status != PlayStatus.None;

    public bool IsPlaying => Status == PlayStatus.Playing;

    /// <summary>Identifies the track, to notice track changes and to cache album art.</summary>
    public string TrackKey => $"{SourceAppId}|{Title}|{Artist}";
}
