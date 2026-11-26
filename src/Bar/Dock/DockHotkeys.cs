using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// Win+1…9 (Win+0 = the 10th) start or bring up the n-th icon of the dock, Shift+Win+n opens a new window of it – like on the
/// Windows taskbar (which TaskWall hides with the full-width bar). A low-level keyboard hook swallows the digit while a Win
/// key is held; the work itself is handed to the UI thread so the hook returns at once.
/// </summary>
static class DockHotkeys
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    const uint LLKHF_INJECTED = 0x10, KEYEVENTF_KEYUP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    static HookProc? _proc; // kept alive for as long as the hook stands
    static IntPtr _hook;
    static DockView? _dock;
    static Dispatcher? _ui;
    static readonly HashSet<uint> _swallowed = new(); // digits whose key-up is swallowed too

    /// <summary>Hooks the keyboard for this dock (a second call only changes the dock).</summary>
    public static void Start(DockView dock)
    {
        _dock = dock;
        if (_hook != IntPtr.Zero) return;
        _ui = dock.Dispatcher;
        _proc = Callback;
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(m?.ModuleName), 0);
        if (_hook == IntPtr.Zero) _proc = null;
    }

    /// <summary>Unhooks the keyboard.</summary>
    public static void Stop()
    {
        _dock = null;
        _swallowed.Clear();
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The digit key's number on the dock: 1–9 are 1–9, 0 is the 10th; 0 when the key is no digit.</summary>
    static int Nth(uint vk) => vk >= 0x31 && vk <= 0x39 ? (int)(vk - 0x30) : vk == 0x30 ? 10 : 0;

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && _dock != null)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int n = Nth(k.vkCode);
                if (n > 0 && (k.flags & LLKHF_INJECTED) == 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if ((Down(VK_LWIN) || Down(VK_RWIN)) && !Down(VK_CONTROL) && !Down(VK_MENU))
                        {
                            bool shift = Down(VK_SHIFT);
                            _swallowed.Add(k.vkCode);
                            // a dummy key while Win is still down: Windows then does not take Win down/up alone for the Start menu
                            keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                            keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            var dock = _dock;
                            _ui?.BeginInvoke(new Action(() => DockView.ForFront(dock)?.ActivateNth(n, shift)));
                            return (IntPtr)1;
                        }
                    }
                    else if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _swallowed.Remove(k.vkCode))
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
