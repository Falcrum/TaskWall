using System;
using System.Threading;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// Notices when the board stops answering: a timer on the UI thread beats every quarter of a second, a background thread
/// watches the beats. When they stop for more than 2 seconds it writes to error.log how long the board hung, what it was
/// doing then (the step named with <see cref="Step"/> around the heavy work: a rebuild, a save, a check of the folder …)
/// and how full the system's memory was – so the next freeze can be told apart from another program's (on 2026-10-30
/// Unity filled the memory and froze everything).
/// </summary>
static class Watchdog
{
    static long _beat = Environment.TickCount64;
    static volatile string _doing = "";

    public static void Start(Dispatcher ui)
    {
        var beat = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        beat.Tick += (_, _) => Interlocked.Exchange(ref _beat, Environment.TickCount64);
        beat.Start();
        new Thread(Watch) { IsBackground = true, Name = "TaskWall watchdog", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Names what the UI thread does until the returned scope ends (it's then what it was before).</summary>
    public static Scope Step(string what)
    {
        var before = _doing;
        _doing = what;
        return new Scope(before);
    }

    public readonly struct Scope : IDisposable
    {
        readonly string _before;
        public Scope(string before) => _before = before;
        public void Dispose() => _doing = _before;
    }

    static void Watch()
    {
        bool hung = false, longReported = false;
        long hungSince = 0;
        string during = "";
        long last = Environment.TickCount64;
        while (true)
        {
            Thread.Sleep(500);
            long now = Environment.TickCount64;
            // the computer slept (or everything stalled, this thread too): not the board's hang – start counting again
            if (now - last > 5000) { Interlocked.Exchange(ref _beat, now); hung = false; last = now; continue; }
            last = now;
            long quiet = now - Interlocked.Read(ref _beat);
            if (!hung && quiet > 2000)
            {
                hung = true;
                longReported = false;
                hungSince = Interlocked.Read(ref _beat);
                during = _doing;
            }
            else if (hung && quiet > 10000 && !longReported)
            {
                // written now as well: a board that never comes back (killed by hand) still leaves a trace
                longReported = true;
                Log.Error($"hang: the board hasn't answered for {quiet / 1000.0:0} s – {What(during)}; {Memory()}");
            }
            else if (hung && quiet < 1000)
            {
                hung = false;
                Log.Error($"hang: the board didn't answer for {(now - hungSince) / 1000.0:0.0} s – {What(during)}; {Memory()}");
            }
        }
    }

    static string What(string during) => during.Length > 0 ? "during: " + during : "outside the named steps";

    static string Memory()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            double load = info.TotalAvailableMemoryBytes > 0 ? 100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes : 0;
            return $"system memory {load:0}% used, the board {Environment.WorkingSet / 1048576} MB";
        }
        catch { return "memory unknown"; }
    }
}
