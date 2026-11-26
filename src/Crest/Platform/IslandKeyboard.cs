using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using static Crest.Platform.NativeMethods;

namespace Crest.Platform;

/// <summary>
/// Lets the no-activate island take the keyboard while a timer name is typed, then gives it back to the app
/// that was in front (decision D-20261125-crest-timers-type-in-island). Outside typing the island never takes focus.
/// </summary>
internal sealed class IslandKeyboard
{
    private IntPtr _previous;

    /// <summary>True while the island may be activated; the island's window procedure reads it.</summary>
    public static bool AllowActivation { get; private set; }

    /// <summary>
    /// Brings the island to the front and puts keyboard focus on <paramref name="element"/>. Call it from a real
    /// click on the island: Windows lets a process take the front only right after it received user input.
    /// </summary>
    public bool Take(IntPtr hwnd, IInputElement element, Dispatcher dispatcher)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!AllowActivation) _previous = GetForegroundWindow();
        AllowActivation = true;
        SetNoActivate(hwnd, on: false);
        bool ok = SetForegroundWindow(hwnd);
        Debug.WriteLine($"IslandKeyboard.Take: SetForegroundWindow={ok}");
        // Focus after the window has handled its activation, so WPF's keyboard focus lands in the text box.
        dispatcher.BeginInvoke(() => Keyboard.Focus(element), DispatcherPriority.Input);
        return ok;
    }

    /// <summary>
    /// Makes the island no-activate again and, if it is still in front, gives the front back to the app that had it
    /// (the same rule as the tray menu). Safe to call more than once.
    /// </summary>
    public void GiveBack(IntPtr hwnd)
    {
        if (!AllowActivation || hwnd == IntPtr.Zero) return;
        AllowActivation = false;
        SetNoActivate(hwnd, on: true);
        var previous = _previous;
        _previous = IntPtr.Zero;
        bool islandInFront = GetForegroundWindow() == hwnd;
        bool ok = islandInFront && previous != IntPtr.Zero && SetForegroundWindow(previous);
        Debug.WriteLine($"IslandKeyboard.GiveBack: islandInFront={islandInFront} restored={ok}");
    }

    private static void SetNoActivate(IntPtr hwnd, bool on)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
}
