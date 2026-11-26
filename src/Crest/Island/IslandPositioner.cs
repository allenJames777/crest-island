using Crest.Platform;
using static Crest.Platform.NativeMethods;

namespace Crest.Island;

/// <summary>One physical display, in physical pixels.</summary>
internal sealed record DisplayInfo(IntPtr Handle, string DeviceName, bool IsPrimary, RECT Bounds, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>
/// Places the island window on a display. Sizes come in DIP and are converted with the
/// target display's own DPI, then applied with SetWindowPos in physical pixels.
/// </summary>
internal static class IslandPositioner
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                uint dpi = GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 ? dx : 96;
                list.Add(new DisplayInfo(h, info.szDevice, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, info.rcMonitor, dpi));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The display saved in settings when it is connected, else the main display (see <see cref="DisplayPicker"/>).</summary>
    public static DisplayInfo? Choose(string? savedDeviceName)
    {
        var displays = GetDisplays();
        int index = DisplayPicker.Pick(displays.Select(d => new DisplayCandidate(d.DeviceName, d.IsPrimary)).ToList(), savedDeviceName);
        return index < 0 ? null : displays[index];
    }

    /// <summary>
    /// Places a window of the given DIP size at the top of the display, slid along the top edge by
    /// <paramref name="horizontalPercent"/> (50 = centre), topmost, without activating it.
    /// </summary>
    public static void Place(IntPtr hwnd, DisplayInfo display, double widthDip, double heightDip, double topOffsetDip,
        int horizontalPercent)
    {
        int w = (int)Math.Round(widthDip * display.Scale);
        int h = (int)Math.Round(heightDip * display.Scale);
        int x = DisplayPicker.WindowLeft(display.Bounds.Left, display.Bounds.Width, w, horizontalPercent);
        int y = display.Bounds.Top + (int)Math.Round(topOffsetDip * display.Scale);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
    }

    /// <summary>Puts the window back on top of other topmost windows, without moving or activating it.</summary>
    public static void KeepOnTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
