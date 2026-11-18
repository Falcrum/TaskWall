using System;
using System.Collections.Generic;

namespace TaskWall;

/// <summary>
/// The Windows taskbar out of the way while the full-width bar is on (Settings → Pasek → Ukryj pasek zadań Windows):
/// it is switched to auto-hide, so it gives its strip of the screen back, and its windows (the main one and those on the
/// other monitors) are hidden; a hidden one that Explorer shows again is hidden again. <see cref="Restore"/> brings it
/// back as it was – also the guard does, after a crash. Explorer stays the shell: nothing of it is replaced.
/// </summary>
static class WindowsTaskbar
{
    const uint EVENT_OBJECT_SHOW = 0x8002;
    static Native.WinEventProc? _proc; // keep the delegate alive
    static IntPtr _hook;
    static bool _autoHideBefore;

    public static bool Hidden { get; private set; }

    /// <summary>The taskbar windows: Shell_TrayWnd and Shell_SecondaryTrayWnd (one per other monitor).</summary>
    static List<IntPtr> Windows()
    {
        var list = new List<IntPtr>();
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        for (var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero && list.Count < 16; h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            list.Add(h);
        return list;
    }

    public static void Hide()
    {
        if (!Hidden)
        {
            Hidden = true;
            _autoHideBefore = AppBar.TaskbarAutoHide;
            Guard.NoteTaskbar(true, _autoHideBefore);
            if (!_autoHideBefore) AppBar.TaskbarAutoHide = true; // its strip goes back to the work area
            if (_hook == IntPtr.Zero)
            {
                _proc = OnShow;
                Native.GetWindowThreadProcessId(Native.FindWindow("Shell_TrayWnd", null), out var explorer);
                _hook = Native.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, explorer, 0, Native.WINEVENT_OUTOFCONTEXT);
            }
        }
        foreach (var h in Windows()) if (Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_HIDE);
    }

    /// <summary>Explorer showed a taskbar window again (a hover at the edge, the Start menu): hidden again.</summary>
    static void OnShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Hidden || idObject != 0) return;
        var cls = Native.ClassName(hwnd);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") Native.ShowWindow(hwnd, Native.SW_HIDE);
    }

    /// <summary>The taskbar as it was before (shown, auto-hide only if it was set before).</summary>
    public static void Restore()
    {
        if (!Hidden) return;
        Hidden = false;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        Restore(_autoHideBefore);
        Guard.NoteTaskbar(false, _autoHideBefore);
    }

    /// <summary>Shows the taskbar windows and puts auto-hide back (the guard, for a TaskWall that crashed).</summary>
    public static void Restore(bool autoHideBefore)
    {
        foreach (var h in Windows()) Native.ShowWindow(h, Native.SW_SHOWNA);
        if (!autoHideBefore) AppBar.TaskbarAutoHide = false;
    }

    /// <summary>Explorer restarted (a new taskbar): hidden again.</summary>
    public static void Rehide()
    {
        if (!Hidden) return;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        Hidden = false;
        Hide();
    }
}
