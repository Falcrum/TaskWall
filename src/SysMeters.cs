using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskWall;

/// <summary>One reading of a load ring: 0–100 % and the lines of its tool tip.</summary>
public sealed record MeterValue(double Percent, string Detail);

/// <summary>
/// The computer's load for the top bar – CPU, GPU, RAM, disks, network – read every 2 seconds on a worker thread from
/// Windows' own performance counters (PDH, what Task Manager reads), only while the bar shows them.
/// </summary>
static class SysMeters
{
    public static readonly string[] Kinds = { "cpu", "gpu", "ram", "disk", "net" };
    /// <summary>A new reading (raised on the worker thread).</summary>
    public static event Action<Dictionary<string, MeterValue>>? Updated;

    static Timer? _timer;
    static IntPtr _query, _cpu, _gpu, _diskIdle, _netRecv, _netSent, _netBw;
    static bool _opened, _busy;
    static readonly object Gate = new();

    public static void Start()
    {
        lock (Gate) _timer ??= new Timer(_ => Tick(), null, 0, 2000);
    }

    public static void Stop()
    {
        lock (Gate) { _timer?.Dispose(); _timer = null; } // the query stays open: started again, it reads right away
    }

    static void Open()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        IntPtr Add(string path) => PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
        _cpu = Add(@"\Processor Information(_Total)\% Processor Utility"); // like Task Manager (turbo counted)
        if (_cpu == IntPtr.Zero) _cpu = Add(@"\Processor(_Total)\% Processor Time");
        _gpu = Add(@"\GPU Engine(*)\Utilization Percentage");
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
        _netRecv = Add(@"\Network Interface(*)\Bytes Received/sec");
        _netSent = Add(@"\Network Interface(*)\Bytes Sent/sec");
        _netBw = Add(@"\Network Interface(*)\Current Bandwidth");
        PdhCollectQueryData(_query); // rates need two samples: the first reading comes with the next tick
    }

    static void Tick()
    {
        lock (Gate) { if (_busy) return; _busy = true; }
        try
        {
            if (!_opened) { _opened = true; Open(); return; }
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return;
            var r = new Dictionary<string, MeterValue>();
            if (Value(_cpu) is { } cpu) r["cpu"] = new MeterValue(Clamp(cpu), L.F("Procesor: {0}%", Math.Round(Clamp(cpu))) + "\n" + L.F("{0} wątków", Environment.ProcessorCount));
            if (Gpu() is { } gpu) r["gpu"] = gpu;
            if (Ram() is { } ram) r["ram"] = ram;
            if (Disk() is { } disk) r["disk"] = disk;
            if (Net() is { } net) r["net"] = net;
            Updated?.Invoke(r);
        }
        catch (Exception ex) { Log.Error("meters", ex); }
        finally { lock (Gate) _busy = false; }
    }

    static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 100);

    /// <summary>The busiest engine of the busiest card (Task Manager's GPU number): processes summed per engine.</summary>
    static MeterValue? Gpu()
    {
        var items = Array(_gpu);
        if (items.Count == 0) return null;
        // "pid_1234_luid_0x0_0xD1F2_phys_0_eng_0_engtype_3D" → per card and engine type
        var engines = items.GroupBy(i =>
        {
            int l = i.Name.IndexOf("luid_", StringComparison.Ordinal), p = i.Name.IndexOf("_phys", StringComparison.Ordinal), e = i.Name.IndexOf("engtype_", StringComparison.Ordinal);
            var luid = l >= 0 && p > l ? i.Name[l..p] : "";
            return (luid, type: e >= 0 ? i.Name[(e + 8)..] : "?");
        }).Select(g => (g.Key.type, load: Clamp(g.Sum(x => x.Value)))).OrderByDescending(x => x.load).ToList();
        var top = engines.GroupBy(x => x.type).Select(g => (type: g.Key, load: g.Max(x => x.load))).OrderByDescending(x => x.load).Take(3);
        return new MeterValue(engines[0].load, L.F("Karta graficzna: {0}%", Math.Round(engines[0].load)) + "\n" + string.Join("  ·  ", top.Select(x => $"{x.type} {Math.Round(x.load)}%")));
    }

    static MeterValue? Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return null;
        double total = m.ullTotalPhys / 1073741824.0, used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return new MeterValue(m.dwMemoryLoad, L.F("Pamięć: {0}%", m.dwMemoryLoad) + "\n" + L.F("zajęte {0} z {1} GB", used.ToString("0.0", L.Culture), total.ToString("0", L.Culture)));
    }

    /// <summary>The busiest disk (active time, like Task Manager); the tool tip lists all of them and the free space.</summary>
    static MeterValue? Disk()
    {
        var disks = Array(_diskIdle).Where(d => d.Name != "_Total").Select(d => (d.Name, busy: Clamp(100 - d.Value))).OrderBy(d => d.Name).ToList();
        if (disks.Count == 0) return null;
        var lines = disks.Select(d => L.F("Dysk {0}: aktywny {1}%", d.Name, Math.Round(d.busy))).ToList();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                lines.Add(L.F("{0} wolne {1} z {2} GB", drive.Name.TrimEnd('\\'), (drive.AvailableFreeSpace / 1073741824.0).ToString("0", L.Culture), (drive.TotalSize / 1073741824.0).ToString("0", L.Culture)));
        }
        catch { /* a drive that went away meanwhile */ }
        return new MeterValue(disks.Max(d => d.busy), string.Join("\n", lines));
    }

    /// <summary>The busiest network link: traffic against its speed; the tool tip has the transfer in both directions.</summary>
    static MeterValue? Net()
    {
        var recv = Array(_netRecv).ToDictionary(x => x.Name, x => x.Value);
        var sent = Array(_netSent).ToDictionary(x => x.Name, x => x.Value);
        var bw = Array(_netBw);
        if (bw.Count == 0) return null;
        var links = bw.Where(b => b.Value > 0).Select(b =>
        {
            double down = recv.GetValueOrDefault(b.Name), up = sent.GetValueOrDefault(b.Name);
            return (b.Name, down, up, speed: b.Value, load: Clamp((down + up) * 8 / b.Value * 100));
        }).OrderByDescending(x => x.down + x.up).ToList();
        if (links.Count == 0) return null;
        var top = links[0];
        string Rate(double bytes) => bytes >= 1048576 ? (bytes / 1048576).ToString("0.0", L.Culture) + " MB/s" : (bytes / 1024).ToString("0", L.Culture) + " kB/s";
        string Speed(double bits) => bits >= 1e9 ? (bits / 1e9).ToString("0.#", L.Culture) + " Gb/s" : (bits / 1e6).ToString("0", L.Culture) + " Mb/s";
        return new MeterValue(top.load, L.F("Sieć: {0}%", Math.Round(top.load)) + $"\n↓ {Rate(links.Sum(x => x.down))}   ↑ {Rate(links.Sum(x => x.up))}\n{top.Name} · {Speed(top.speed)}");
    }

    static double? Value(IntPtr counter) =>
        counter != IntPtr.Zero && PdhGetFormattedCounterValue(counter, Fmt, out _, out var v) == 0 && v.Status <= 1 ? v.Value : null;

    static List<(string Name, double Value)> Array(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, Fmt, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, Fmt, ref size, out var count, buf) != 0) return list;
            int step = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var it = Marshal.PtrToStructure<Item>(buf + i * step);
                if (it.Value.Status <= 1) list.Add((Marshal.PtrToStringUni(it.Name) ?? "", it.Value.Value)); // valid / new data
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    const uint Fmt = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    const int MoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)] struct FmtValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)] struct Item { public IntPtr Name; public FmtValue Value; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
