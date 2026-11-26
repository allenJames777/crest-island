namespace Crest.Media;

/// <summary>One app with a media session, as the picker sees it.</summary>
public sealed record SessionInfo(string Id, PlayStatus Status);

/// <summary>
/// Chooses which media session the island shows when several apps have media
/// (decision D-20261124-crest-smart-session-pick):
/// the user's switched choice while its app is still open; else Windows' current session if it is playing;
/// else the first session that is playing; else Windows' current session; else the first session.
/// </summary>
public sealed class SessionPicker
{
    /// <summary>The session the user switched to, kept until that app closes.</summary>
    public string? Pinned { get; private set; }

    public string? Pick(IReadOnlyList<SessionInfo> sessions, string? windowsCurrentId)
    {
        if (Pinned is not null && !sessions.Any(s => s.Id == Pinned)) Pinned = null;
        if (sessions.Count == 0) return null;
        if (Pinned is not null) return Pinned;

        var current = sessions.FirstOrDefault(s => s.Id == windowsCurrentId);
        if (current is { Status: PlayStatus.Playing }) return current.Id;
        var playing = sessions.FirstOrDefault(s => s.Status == PlayStatus.Playing);
        if (playing is not null) return playing.Id;
        return current?.Id ?? sessions[0].Id;
    }

    /// <summary>Moves to the session after the shown one (wrapping) and keeps it as the user's choice.</summary>
    public string? Next(IReadOnlyList<SessionInfo> sessions, string? shownId)
    {
        if (sessions.Count == 0) return null;
        int index = -1;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].Id == shownId) { index = i; break; }
        Pinned = sessions[(index + 1) % sessions.Count].Id;
        return Pinned;
    }
}
