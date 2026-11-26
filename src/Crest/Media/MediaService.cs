using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GsmtcStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Crest.Media;

/// <summary>
/// Watches every app with a media session, shows the one <see cref="SessionPicker"/> picks (the playing one, or the
/// app the user switched to), and publishes a <see cref="NowPlaying"/> snapshot on the UI thread whenever it changes.
/// This is the only class that talks to the Windows media API.
/// </summary>
internal sealed class MediaService : IDisposable
{
    private readonly Dispatcher _ui;
    private GsmtcManager? _manager;
    private GsmtcSession? _session;
    private readonly SessionPicker _picker = new();
    private List<GsmtcSession> _watched = [];
    private int _sessionIndex;
    private int _sessionCount;
    private int _version;
    private bool _refreshQueued;
    private bool _fullRefreshPending;
    private string _artKey = "";
    private BitmapSource? _art;
    private TimeSpan _timelineStart;

    public MediaService(Dispatcher ui) => _ui = ui;

    public NowPlaying Current { get; private set; } = NowPlaying.Empty;

    /// <summary>Raised on the UI thread with each new snapshot.</summary>
    public event Action<NowPlaying>? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GsmtcManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Crest: media controls unavailable: {ex.Message}");
            Publish(NowPlaying.Empty);
            return;
        }
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        WatchSessions();
        ChooseSession();
    }

    /// <summary>Re-reads every session, for example after the PC wakes or the user unlocks it.</summary>
    public void Refresh()
    {
        WatchSessions();
        ChooseSession();
    }

    /// <summary>Shows the next app with media (the island's app chip), and keeps it until that app closes.</summary>
    public void ShowNextApp()
    {
        var sessions = ReadSessions();
        string? shown = sessions.FirstOrDefault(s => ReferenceEquals(s.Session, _session)).Info?.Id;
        string? id = _picker.Next(sessions.Select(s => s.Info).ToList(), shown);
        Show(sessions, id);
    }

    public Task TogglePlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync(), Current.Can.PlayPause);
    public Task NextAsync() => RunAsync(s => s.TrySkipNextAsync(), Current.Can.Next);
    public Task PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync(), Current.Can.Previous);

    public Task SeekAsync(TimeSpan position) =>
        RunAsync(s => s.TryChangePlaybackPositionAsync((_timelineStart + position).Ticks), Current.Can.Seek);

    public Task ToggleShuffleAsync() =>
        RunAsync(s => s.TryChangeShuffleActiveAsync(!(Current.ShuffleOn ?? false)), Current.Can.Shuffle);

    public Task CycleRepeatAsync()
    {
        var next = Current.Repeat switch
        {
            RepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            RepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return RunAsync(s => s.TryChangeAutoRepeatModeAsync(next), Current.Can.Repeat);
    }

    public void Dispose()
    {
        _timelineThrottle?.Stop();
        Detach();
        Unwatch();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        _manager = null;
    }

    // Windows raises these on background threads; everything below runs on the UI thread.
    private void OnCurrentSessionChanged(GsmtcManager sender, CurrentSessionChangedEventArgs args) =>
        _ui.InvokeAsync(ChooseSession);

    private void OnSessionsChanged(GsmtcManager sender, SessionsChangedEventArgs args) =>
        _ui.InvokeAsync(() => { WatchSessions(); ChooseSession(); });

    /// <summary>Any app starting or pausing can change which one is shown.</summary>
    private void OnAnyPlaybackChanged(GsmtcSession sender, PlaybackInfoChangedEventArgs args) => _ui.InvokeAsync(ChooseSession);

    private void OnMediaPropertiesChanged(GsmtcSession sender, MediaPropertiesChangedEventArgs args) => QueueRefresh(full: true);

    // Apps report the timeline often (Spotify about once a second even while paused, browsers more often): only
    // re-read play state and timeline, at most once a second; the island estimates the position in between.
    private void OnTimelinePropertiesChanged(GsmtcSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ui.InvokeAsync(QueueTimelineRefresh);

    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(1);
    private DateTimeOffset _lastTimelineRefresh = DateTimeOffset.MinValue;
    private DispatcherTimer? _timelineThrottle;

    private void QueueTimelineRefresh()
    {
        if (_timelineThrottle?.IsEnabled == true) return; // one is already due
        var since = DateTimeOffset.Now - _lastTimelineRefresh;
        if (since >= TimelineInterval)
        {
            _lastTimelineRefresh = DateTimeOffset.Now;
            QueueRefresh(full: false);
            return;
        }
        if (_timelineThrottle is null)
        {
            _timelineThrottle = new DispatcherTimer(DispatcherPriority.Background, _ui);
            _timelineThrottle.Tick += (_, _) =>
            {
                _timelineThrottle!.Stop();
                _lastTimelineRefresh = DateTimeOffset.Now;
                QueueRefresh(full: false);
            };
        }
        _timelineThrottle.Interval = TimelineInterval - since;
        _timelineThrottle.Start();
    }

    /// <summary>Coalesces refresh requests; a full refresh re-reads the song, a light one only play state and timeline.</summary>
    private void QueueRefresh(bool full) => _ui.InvokeAsync(() =>
    {
        _fullRefreshPending |= full;
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.InvokeAsync(() =>
        {
            _refreshQueued = false;
            bool fullRefresh = _fullRefreshPending || !Current.HasSession;
            _fullRefreshPending = false;
            if (fullRefresh) _ = RefreshAsync();
            else RefreshPlayback();
        }, DispatcherPriority.Background);
    });

    /// <summary>Re-reads play state, controls and timeline of the shown session, keeping the song details.</summary>
    private void RefreshPlayback()
    {
        var session = _session;
        if (session is null)
        {
            Publish(NowPlaying.Empty);
            return;
        }
        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            Publish(Current with
            {
                Status = MapStatus(playback?.PlaybackStatus),
                Timeline = MapTimeline(timeline, playback),
                Can = MapCapabilities(playback),
                ShuffleOn = playback?.IsShuffleActive,
                Repeat = MapRepeat(playback),
                SessionIndex = _sessionIndex,
                SessionCount = _sessionCount,
            });
        }
        catch (Exception ex) { Debug.WriteLine($"Crest: playback info failed: {ex.Message}"); }
    }

    /// <summary>Subscribes to playback changes of every session, so a newly playing app is noticed.</summary>
    private void WatchSessions()
    {
        Unwatch();
        try { _watched = _manager?.GetSessions().ToList() ?? []; }
        catch (Exception ex) { Debug.WriteLine($"Crest: could not list sessions: {ex.Message}"); _watched = []; }
        // A session whose app is closing can refuse the subscription; skip it rather than fail.
        _watched = _watched.Where(session => Try(() => session.PlaybackInfoChanged += OnAnyPlaybackChanged)).ToList();
    }

    private void Unwatch()
    {
        foreach (var session in _watched) Try(() => session.PlaybackInfoChanged -= OnAnyPlaybackChanged);
        _watched = [];
    }

    /// <summary>Runs a call on a session object, which throws once its app has gone; returns whether it worked.</summary>
    private static bool Try(Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"Crest: a media session call failed: {ex.Message}");
            return false;
        }
    }

    private List<(GsmtcSession Session, SessionInfo Info)> ReadSessions()
    {
        var list = new List<(GsmtcSession, SessionInfo)>();
        foreach (var session in _watched)
        {
            try { list.Add((session, new SessionInfo(Id(session) ?? "", MapStatus(session.GetPlaybackInfo()?.PlaybackStatus)))); }
            catch (Exception ex) { Debug.WriteLine($"Crest: could not read a session: {ex.Message}"); }
        }
        return list;
    }

    private void ChooseSession()
    {
        var sessions = ReadSessions();
        string? currentId = null;
        try { currentId = Id(_manager?.GetCurrentSession()); }
        catch (Exception ex) { Debug.WriteLine($"Crest: could not read the current session: {ex.Message}"); }
        Show(sessions, _picker.Pick(sessions.Select(s => s.Info).ToList(), currentId));
    }

    /// <summary>Shows the session with the given id (attaching its events if it changed) and refreshes.</summary>
    private void Show(List<(GsmtcSession Session, SessionInfo Info)> sessions, string? id)
    {
        int index = sessions.FindIndex(s => s.Info.Id == id);
        var target = index >= 0 ? sessions[index].Session : null;
        _sessionCount = sessions.Count;
        _sessionIndex = index + 1;
        bool changed = !ReferenceEquals(target, _session);
        if (changed)
        {
            _version++; // a refresh still running for the old session must not publish
            Detach();
            _session = target;
            var attached = _session;
            if (attached is not null)
            {
                Try(() =>
                {
                    attached.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    attached.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                });
            }
        }
        QueueRefresh(full: changed);
    }

    private void Detach()
    {
        var session = _session;
        if (session is null) return;
        Try(() => session.MediaPropertiesChanged -= OnMediaPropertiesChanged);
        Try(() => session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged);
        _session = null;
    }

    private static string? Id(GsmtcSession? session) => session?.SourceAppUserModelId;

    private async Task RefreshAsync()
    {
        int version = ++_version;
        var session = _session;
        if (session is null)
        {
            Publish(NowPlaying.Empty);
            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Crest: media properties failed: {ex.Message}"); }
        if (version != _version || !ReferenceEquals(session, _session)) return;

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
        string appId = "";
        try
        {
            playback = session.GetPlaybackInfo();
            timeline = session.GetTimelineProperties();
            appId = session.SourceAppUserModelId ?? "";
        }
        catch (Exception ex) { Debug.WriteLine($"Crest: playback info failed: {ex.Message}"); }

        string title = props?.Title ?? "";
        string artist = string.IsNullOrEmpty(props?.Artist) ? props?.AlbumArtist ?? "" : props!.Artist;
        string key = $"{appId}|{title}|{artist}";
        if (key != _artKey)
        {
            _artKey = key;
            _art = null;
        }
        // Apps such as Spotify often send the thumbnail late or not at all on the first event,
        // so keep trying on later events for the same track while the art is still missing.
        if (_art is null && props?.Thumbnail is { } thumbnail)
        {
            var art = await ReadArtAsync(thumbnail);
            if (key == _artKey) _art = art;
            if (version != _version || !ReferenceEquals(session, _session)) return;
        }

        Publish(new NowPlaying(
            SourceAppId: appId,
            SourceAppName: FriendlyAppName(appId),
            Title: title,
            Artist: artist,
            AlbumTitle: props?.AlbumTitle ?? "",
            Art: _art,
            Status: MapStatus(playback?.PlaybackStatus),
            Timeline: MapTimeline(timeline, playback),
            Can: MapCapabilities(playback),
            ShuffleOn: playback?.IsShuffleActive,
            Repeat: MapRepeat(playback),
            SessionIndex: _sessionIndex,
            SessionCount: _sessionCount));
    }

    /// <summary>Publishes a snapshot unless nothing the island shows has changed.</summary>
    private void Publish(NowPlaying snapshot)
    {
        if (SameForDisplay(Current, snapshot)) return;
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// Equal, ignoring a timeline report that only restamps the same position (what a paused app sends repeatedly).
    /// </summary>
    internal static bool SameForDisplay(NowPlaying a, NowPlaying b)
    {
        static NowPlaying WithoutStamp(NowPlaying n) =>
            n.Timeline is null ? n : n with { Timeline = n.Timeline with { LastUpdated = default } };
        bool samePosition = a.Timeline?.Position == b.Timeline?.Position;
        return samePosition && a.Status != PlayStatus.Playing && WithoutStamp(a) == WithoutStamp(b)
            || a == b;
    }

    private static RepeatMode? MapRepeat(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback) =>
        playback?.AutoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => RepeatMode.None,
            MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track,
            MediaPlaybackAutoRepeatMode.List => RepeatMode.List,
            _ => null,
        };

    private TimelineInfo? MapTimeline(
        GlobalSystemMediaTransportControlsSessionTimelineProperties? t,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        if (t is null) return null;
        _timelineStart = t.StartTime;
        var duration = t.EndTime - t.StartTime;
        if (duration <= TimeSpan.Zero) return null;
        return new TimelineInfo(t.Position - t.StartTime, duration, t.LastUpdatedTime, playback?.PlaybackRate ?? 1.0);
    }

    private static PlayStatus MapStatus(GsmtcStatus? status) => status switch
    {
        GsmtcStatus.Playing or GsmtcStatus.Changing => PlayStatus.Playing,
        GsmtcStatus.Paused => PlayStatus.Paused,
        GsmtcStatus.Stopped or GsmtcStatus.Opened => PlayStatus.Stopped,
        _ => PlayStatus.None,
    };

    private static MediaCapabilities MapCapabilities(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        var c = playback?.Controls;
        if (c is null) return MediaCapabilities.None;
        return new MediaCapabilities(c.IsPlayPauseToggleEnabled, c.IsNextEnabled, c.IsPreviousEnabled,
            c.IsPlaybackPositionEnabled, c.IsShuffleEnabled, c.IsRepeatEnabled);
    }

    private static async Task<BitmapSource?> ReadArtAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var input = winStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Crest: album art failed: {ex.Message}");
            return null;
        }
    }

    private async Task RunAsync(Func<GsmtcSession, IAsyncOperation<bool>> command, bool supported)
    {
        var session = _session;
        if (session is null || !supported) return;
        try { await command(session); }
        catch (Exception ex) { Debug.WriteLine($"Crest: media command failed: {ex.Message}"); }
    }

    /// <summary>A readable name for the app that is playing, from its app id (best effort).</summary>
    internal static string FriendlyAppName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        string id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("firefox") || id.Contains("308046b0af4a39cb")) return "Firefox";
        if (id.Contains("applemusic")) return "Apple Music";
        if (id.Contains("zunemusic")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";

        string name = appId;
        int bang = name.IndexOf('!');
        if (bang >= 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore >= 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }
}
