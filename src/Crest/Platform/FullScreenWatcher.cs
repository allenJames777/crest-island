using System.Text;
using static Crest.Platform.NativeMethods;

namespace Crest.Platform;

/// <summary>
/// Notices when an app goes full screen on the island's display (games, full-screen video, F11 in a browser,
/// presentations). Event-driven: it listens for foreground changes and for the foreground window changing size,
/// with no polling. Must be created on the UI thread, which delivers the hook callbacks.
/// </summary>
internal sealed class FullScreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly Func<IntPtr> _islandWindow;
    private readonly WinEventDelegate _callback; // kept in a field so the garbage collector cannot free it while hooked
    private readonly uint _ownProcess = (uint)Environment.ProcessId;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;

    public FullScreenWatcher(Func<IntPtr> islandWindow)
    {
        _islandWindow = islandWindow;
        _callback = OnWinEvent;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, Flags);
        WatchForegroundProcess();
        IsFullScreen = Detect();
    }

    private const uint Flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

    /// <summary>
    /// Size changes are watched only in the app that is in front, so Crest is not woken by every window and cursor
    /// movement on the PC; the hook moves with the foreground.
    /// </summary>
    private void WatchForegroundProcess()
    {
        if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
        _locationHook = IntPtr.Zero;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == _ownProcess) return;
        _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _callback, pid, 0, Flags);
    }

    public bool IsFullScreen { get; private set; }

    /// <summary>Raised when full screen starts or ends on the island's display.</summary>
    public event Action<bool>? Changed;

    /// <summary>Raised when another window comes to the front (the island then re-asserts topmost).</summary>
    public event Action? ForegroundChanged;

    /// <summary>Checks again, for example after a display change.</summary>
    public void Recheck()
    {
        bool now = Detect();
        if (now == IsFullScreen) return;
        IsFullScreen = now;
        Changed?.Invoke(now);
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
        _foregroundHook = _locationHook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
        {
            // This fires for every moving thing on screen (the cursor too); keep only the foreground window itself.
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd != GetForegroundWindow()) return;
        }
        else
        {
            WatchForegroundProcess();
            ForegroundChanged?.Invoke();
        }
        Recheck();
    }

    /// <summary>
    /// The foreground window covers the whole display the island is on, and it is not the desktop or taskbar,
    /// not Crest itself, and not just a maximized window with a title bar. Windows' own "full-screen app or
    /// presentation" state also counts when the foreground window is on the island's display.
    /// </summary>
    private bool Detect()
    {
        IntPtr island = _islandWindow();
        IntPtr fg = GetForegroundWindow();
        if (island == IntPtr.Zero || fg == IntPtr.Zero) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownProcess) return false;

        var name = new StringBuilder(64);
        GetClassName(fg, name, name.Capacity);
        if (ShellClasses.Contains(name.ToString())) return false;

        IntPtr monitor = MonitorFromWindow(island, MONITOR_DEFAULTTONEAREST);
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor) return false;
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var m = info.rcMonitor;

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(fg, out r);

        bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        if (covers)
        {
            long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
            if (IsZoomed(fg) && (style & WS_CAPTION) == WS_CAPTION) covers = false; // maximized with a title bar
        }
        if (!covers && SHQueryUserNotificationState(out int state) == 0)
            covers = state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        return covers;
    }
}
