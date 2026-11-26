namespace Crest.Platform;

/// <summary>A display as the picker sees it.</summary>
public sealed record DisplayCandidate(string DeviceName, bool IsPrimary);

/// <summary>Pure logic for where the island goes: which display, and how far along its top edge.</summary>
public static class DisplayPicker
{
    /// <summary>
    /// The chosen display when it is connected (names compared without case), else the main display, else the first.
    /// Returns -1 when there are no displays.
    /// </summary>
    public static int Pick(IReadOnlyList<DisplayCandidate> displays, string? saved)
    {
        if (displays.Count == 0) return -1;
        if (!string.IsNullOrWhiteSpace(saved))
        {
            for (int i = 0; i < displays.Count; i++)
                if (string.Equals(displays[i].DeviceName, saved, StringComparison.OrdinalIgnoreCase)) return i;
        }
        for (int i = 0; i < displays.Count; i++)
            if (displays[i].IsPrimary) return i;
        return 0;
    }

    /// <summary>
    /// Left edge of the island window, in pixels: 0% puts it at the display's left edge, 50% centres it, 100% puts it
    /// at the right edge; the window always stays fully on the display.
    /// </summary>
    public static int WindowLeft(int displayLeft, int displayWidth, int windowWidth, int percent)
    {
        int room = Math.Max(0, displayWidth - windowWidth);
        return displayLeft + (int)Math.Round(room * Math.Clamp(percent, 0, 100) / 100.0);
    }
}
