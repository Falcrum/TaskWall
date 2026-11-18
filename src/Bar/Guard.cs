using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TaskWall;

/// <summary>
/// Keeps the full-width bar from outliving TaskWall: while the bar holds its strip of the screen, a second copy of
/// TaskWall.exe runs as a guard (<c>--guard</c>) and waits for the main one to exit. A clean exit leaves a mark and the
/// guard just ends; a crash (or the process killed) – the guard gives the strip back (ABM_REMOVE for the dead bar's
/// window) and notes the crash. Three crashes in ten minutes: the next start goes without the bar (the old top bar
/// instead) – the idea of Rustyfiles' shell guard.
/// </summary>
static class Guard
{
    sealed class State
    {
        public int Pid { get; set; }
        public long Bar { get; set; }
        public bool Clean { get; set; }
        public List<DateTime> Crashes { get; set; } = new();
    }

    static readonly object Gate = new();
    static Process? _guard;
    static IntPtr _watched;

    static string File =>
        Dev.Arg("--data") is { } dev ? Path.Combine(dev, "guard.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskWall", "guard.json");

    static State Read()
    {
        try { return JsonSerializer.Deserialize<State>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch { return new(); }
    }

    static void Write(State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s));
        }
        catch (Exception ex) { Log.Error("guard state", ex); }
    }

    /// <summary>The bar crashed TaskWall three times in ten minutes: start without it this time.</summary>
    public static bool CrashLoop() => CrashLoopCheck(Read().Crashes, DateTime.Now);

    static bool CrashLoopCheck(IEnumerable<DateTime> crashes, DateTime now) => TaskWall.CrashLoop.TooMany(crashes, now);

    /// <summary>
    /// The bar's window now holds a strip (<see cref="IntPtr.Zero"/>: none any more). The guard starts with the first
    /// strip and keeps running until TaskWall exits.
    /// </summary>
    public static void Watch(IntPtr bar)
    {
        lock (Gate)
        {
            if (bar == _watched && (bar == IntPtr.Zero || _guard is { HasExited: false })) return; // nothing new (called on every layout)
            _watched = bar;
            var s = Read();
            s.Pid = Environment.ProcessId;
            s.Bar = bar.ToInt64();
            s.Clean = false;
            Write(s);
            if (bar == IntPtr.Zero || (_guard is { HasExited: false })) return;
            try
            {
                var args = $"--guard {Environment.ProcessId}" + (Dev.Arg("--data") is { } dev ? $" --data \"{dev}\"" : "");
                _guard = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) { Log.Error("guard start", ex); }
        }
    }

    /// <summary>TaskWall exits normally (the bar already gave its strip back): the guard has nothing to do.</summary>
    public static void CleanExit()
    {
        lock (Gate)
        {
            var s = Read();
            if (s.Pid != Environment.ProcessId) return;
            s.Clean = true;
            s.Bar = 0;
            Write(s);
        }
    }

    /// <summary>The guard process: waits for TaskWall <paramref name="pid"/>, then cleans up after a crash.</summary>
    public static void Run(int pid)
    {
        try
        {
            using var main = Process.GetProcessById(pid);
            main.WaitForExit();
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Log.Error("guard wait", ex); }
        var s = Read();
        if (s.Pid != pid || s.Clean) return;
        if (s.Bar != 0)
        {
            AppBar.Remove(new IntPtr(s.Bar)); // the strip of a window that no longer exists
            var now = DateTime.Now;
            s.Crashes = TaskWall.CrashLoop.Recent(s.Crashes.Append(now), now, TimeSpan.FromHours(1));
            Log.Error($"guard: TaskWall (pid {pid}) ended without closing – the bar's strip given back" + (CrashLoopCheck(s.Crashes, now) ? "; three crashes in 10 minutes – the next start goes without the full-width bar" : ""));
        }
        s.Bar = 0;
        s.Clean = true;
        Write(s);
    }
}
