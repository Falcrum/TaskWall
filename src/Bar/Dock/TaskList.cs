using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskWall;

/// <summary>
/// The windows a taskbar shows, found with Explorer's rules (<see cref="TaskRules"/>): visible, not cloaked by DWM (other virtual
/// desktops, suspended apps), a button by their style, not an untitled helper. Grouped by the AppUserModelID or the program.
/// Read-only: nothing here changes a window.
/// </summary>
static class TaskList
{
    public sealed record Task(IntPtr Hwnd, string Title, uint Pid, string? Exe, string? AppId, bool Minimized)
    {
        public string Group => TaskRules.Group(AppId, Exe, Pid);
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder sb, ref uint size);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
    const uint GW_OWNER = 4, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid Fmtid; public uint Pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P; public IntPtr P2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    static PropertyKey AppUserModelId = new() { Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };

    /// <summary>The task windows now, the top of the z-order first.</summary>
    public static List<Task> Tasks()
    {
        var list = new List<Task>();
        EnumWindows((h, _) => { if (Read(h) is { } t) list.Add(t); return true; }, IntPtr.Zero);
        return list;
    }

    public static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var v, 4) == 0 && v != 0;

    static Task? Read(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return null;
        uint ex = (uint)GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool owner = Native.GetWindow(h, GW_OWNER) != IntPtr.Zero;
        if (!TaskRules.StyleIsTask(owner, ex) || Cloaked(h)) return null;
        Native.GetWindowRect(h, out var r);
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == (uint)Environment.ProcessId) return null; // TaskWall's own windows are not tasks of the dock
        var title = Title(h);
        bool min = IsIconic(h);
        if (TaskRules.IsHelper(title, r.Right - r.Left, r.Bottom - r.Top, min)) return null;
        return new Task(h, title, pid, ExeOf(pid), AppIdOf(h), min);
    }

    public static string Title(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static readonly Dictionary<uint, string?> Exes = new();

    /// <summary>The program a process runs (null for one that can't be opened – an elevated one of another user).</summary>
    public static string? ExeOf(uint pid)
    {
        if (Exes.TryGetValue(pid, out var known)) return known;
        string? exe = null;
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p != IntPtr.Zero)
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageName(p, 0, sb, ref size)) exe = sb.ToString();
            CloseHandle(p);
        }
        if (Exes.Count > 500) Exes.Clear(); // process ids are reused: keep it short
        return Exes[pid] = exe;
    }

    /// <summary>The window's AppUserModelID (System.AppUserModel.ID in its property store), when it sets one.</summary>
    static string? AppIdOf(IntPtr h)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out var store) != 0 || store == null) return null;
            try
            {
                var key = AppUserModelId;
                if (store.GetValue(ref key, out var pv) != 0) return null;
                try { return pv.Vt == 31 /* VT_LPWSTR */ && pv.P != IntPtr.Zero ? Marshal.PtrToStringUni(pv.P) is { Length: > 0 } s ? s : null : null; }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }
}
