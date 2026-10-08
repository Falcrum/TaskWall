using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TaskWall;

public sealed class MonitorInfo
{
    public required string Device { get; init; }
    public required Int32Rect Bounds { get; init; }   // physical pixels
    public required Int32Rect Work { get; init; }     // physical pixels
    public required double Scale { get; init; }       // DPI / 96
    public required bool Primary { get; init; }

    public string Label(int index) =>
        $"Monitor {index + 1}{(Primary ? " (główny)" : "")} – {Bounds.Width}×{Bounds.Height}";
}

static class Monitors
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr hmon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

    static Int32Rect R(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hmon, ref mi)) return true;
            double scale = GetDpiForMonitor(hmon, 0, out var dx, out _) == 0 ? dx / 96.0 : 1;
            list.Add(new MonitorInfo { Device = mi.szDevice, Bounds = R(mi.rcMonitor), Work = R(mi.rcWork), Scale = scale, Primary = (mi.dwFlags & 1) != 0 });
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    public static MonitorInfo Pick(string device)
    {
        var all = All();
        return all.FirstOrDefault(m => m.Device == device) ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault()
            // during display reconfiguration the list can briefly be empty
            ?? new MonitorInfo { Device = "", Bounds = new Int32Rect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight),
                                 Work = new Int32Rect(0, 0, (int)SystemParameters.WorkArea.Width, (int)SystemParameters.WorkArea.Height), Scale = 1, Primary = true };
    }
}

/// <summary>
/// Frosted glass done by hand: find the wallpaper behind a window, cut that piece out,
/// blur it and use it as the window background (like Todowall). Works on every Windows build,
/// unlike the system blur that doesn't work on per-pixel transparent windows.
/// </summary>
static class Wallpaper
{
    public enum Position { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(Position position);
        Position GetPosition();
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaperClass { }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    public sealed class Source
    {
        /// <summary>Small copy (max 1280 px) – enough for the blur and the accent colour.</summary>
        public BitmapSource? Image;
        /// <summary>Size of the original file (placement maths uses it).</summary>
        public int PixelWidth, PixelHeight;
        public string? Path;
        public Position Pos = Position.Fill;
        public Color Background = Colors.Black;
        public Int32Rect Monitor;
        public Int32Rect Span;
        public string Signature = "";
    }

    static readonly string ThemesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");
    static Source? _cache;

    static IDesktopWallpaper? Com()
    {
        try { return (IDesktopWallpaper)new DesktopWallpaperClass(); }
        catch (Exception ex) { Log.Error("IDesktopWallpaper", ex); return null; }
    }

    /// <summary>Cheap fingerprint used to notice wallpaper changes (incl. slideshow / Spotlight).</summary>
    public static string Signature(MonitorInfo m)
    {
        var (path, pos, bg, _) = Describe(m);
        string Stamp(string? p) => p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString() : "-";
        return $"{path}|{Stamp(path)}|{pos}|{bg}|{Stamp(Path.Combine(ThemesDir, "TranscodedWallpaper"))}|{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height}";
    }

    static (string? path, Position pos, uint bg, int index) Describe(MonitorInfo m)
    {
        string? path = null;
        var pos = Position.Fill;
        uint bg = 0;
        int index = -1;
        var dw = Com();
        if (dw != null)
        {
            try
            {
                pos = dw.GetPosition();
                bg = dw.GetBackgroundColor();
                uint n = dw.GetMonitorDevicePathCount();
                for (uint i = 0; i < n; i++)
                {
                    try
                    {
                        var id = dw.GetMonitorDevicePathAt(i);
                        var r = dw.GetMonitorRECT(id);
                        if (r.Left == m.Bounds.X && r.Top == m.Bounds.Y)
                        {
                            index = (int)i;
                            var p = dw.GetWallpaper(id);
                            if (!string.IsNullOrEmpty(p) && File.Exists(p)) path = p;
                            break;
                        }
                    }
                    catch { /* inactive monitor */ }
                }
            }
            catch (Exception ex) { Log.Error("wallpaper describe", ex); }
            finally { Marshal.ReleaseComObject(dw); }
        }
        if (path == null)
        {
            // slideshow / Spotlight / images Windows keeps only as transcoded copies
            foreach (var cand in new[] { index >= 0 ? $"Transcoded_{index:000}" : null, "TranscodedWallpaper" })
            {
                if (cand == null) continue;
                var p = Path.Combine(ThemesDir, cand);
                if (File.Exists(p)) { path = p; break; }
            }
        }
        return (path, pos, bg, index);
    }

    public static void Invalidate() => _cache = null;

    public static Source Resolve(MonitorInfo m)
    {
        var sig = Signature(m);
        if (_cache != null && _cache.Signature == sig) return _cache;

        var (path, pos, bg, _) = Describe(m);
        var s = new Source
        {
            Pos = pos,
            Background = Color.FromRgb((byte)(bg & 0xFF), (byte)((bg >> 8) & 0xFF), (byte)((bg >> 16) & 0xFF)),
            Monitor = m.Bounds,
            Signature = sig,
        };
        var all = Monitors.All();
        int minX = all.Min(x => x.Bounds.X), minY = all.Min(x => x.Bounds.Y);
        int maxX = all.Max(x => x.Bounds.X + x.Bounds.Width), maxY = all.Max(x => x.Bounds.Y + x.Bounds.Height);
        s.Span = new Int32Rect(minX, minY, maxX - minX, maxY - minY);

        if (path != null)
        {
            try
            {
                s.Path = path;
                s.Image = Load(path, 1280, out s.PixelWidth, out s.PixelHeight);
            }
            catch (Exception ex) { Log.Error("wallpaper load " + path, ex); }
        }
        _cache = s;
        return s;
    }

    /// <summary>Decodes the file scaled down to <paramref name="maxSide"/> (0 = full size); JPEG decoding scales natively, so it's cheap.</summary>
    static BitmapSource Load(string path, int maxSide, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        width = header.PixelWidth;
        height = header.PixelHeight;
        fs.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = fs;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (maxSide > 0 && Math.Max(width, height) > maxSide)
        {
            if (width >= height) bi.DecodePixelWidth = maxSide;
            else bi.DecodePixelHeight = maxSide;
        }
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>
    /// Sharp wallpaper squares (size <paramref name="c"/> px) for the four corners of <paramref name="win"/>:
    /// TL, TR, BL, BR. The full-size image is decoded only for this and dropped right after.
    /// </summary>
    public static BitmapSource?[] RenderCorners(Source s, Int32Rect win, int c)
    {
        var result = new BitmapSource?[4];
        if (c <= 0 || win.Width <= 0) return result;
        var full = s;
        if (s.Path != null)
        {
            try { full = new Source { Image = Load(s.Path, 0, out _, out _), PixelWidth = s.PixelWidth, PixelHeight = s.PixelHeight, Pos = s.Pos, Background = s.Background, Monitor = s.Monitor, Span = s.Span }; }
            catch (Exception ex) { Log.Error("wallpaper corners", ex); }
        }
        var spots = new[] { (win.X, win.Y), (win.X + win.Width - c, win.Y), (win.X, win.Y + win.Height - c), (win.X + win.Width - c, win.Y + win.Height - c) };
        for (int i = 0; i < 4; i++) result[i] = Render(full, new Int32Rect(spots[i].Item1, spots[i].Item2, c, c), 0, 1);
        return result;
    }

    /// <summary>Where the wallpaper image lands on the desktop, in physical pixels.</summary>
    static IEnumerable<Rect> Placements(Source s, Int32Rect? within = null)
    {
        if (s.Image == null || s.PixelWidth == 0) yield break;
        double iw = s.PixelWidth, ih = s.PixelHeight;
        var m = s.Pos == Position.Span ? s.Span : s.Monitor;
        switch (s.Pos)
        {
            case Position.Stretch:
                yield return new Rect(m.X, m.Y, m.Width, m.Height);
                break;
            case Position.Center:
                yield return new Rect(m.X + (m.Width - iw) / 2, m.Y + (m.Height - ih) / 2, iw, ih);
                break;
            case Position.Tile:
                var area = within is { } w ? new Rect(w.X, w.Y, w.Width, w.Height) : new Rect(m.X, m.Y, m.Width, m.Height);
                double x0 = m.X + Math.Floor((Math.Max(area.X, m.X) - m.X) / iw) * iw;
                double y0 = m.Y + Math.Floor((Math.Max(area.Y, m.Y) - m.Y) / ih) * ih;
                for (double y = y0; y < Math.Min(m.Y + m.Height, area.Bottom); y += ih)
                    for (double x = x0; x < Math.Min(m.X + m.Width, area.Right); x += iw)
                        yield return new Rect(x, y, iw, ih);
                break;
            default:
                double k = s.Pos == Position.Fit ? Math.Min(m.Width / iw, m.Height / ih) : Math.Max(m.Width / iw, m.Height / ih);
                yield return new Rect(m.X + (m.Width - iw * k) / 2, m.Y + (m.Height - ih * k) / 2, iw * k, ih * k);
                break;
        }
    }

    /// <summary>Blurred piece of the wallpaper behind <paramref name="win"/> (physical pixels).</summary>
    public static BitmapSource? RenderGlass(Source s, Int32Rect win, double blurPx, double k = 0.25) => Render(s, win, blurPx, k); // blur at quarter resolution: cheap and visually identical


    static BitmapSource? Render(Source s, Int32Rect win, double blurPx, double k)
    {
        if (win.Width <= 0 || win.Height <= 0) return null;
        int margin = (int)Math.Ceiling(blurPx * k) + 2;
        int w = Math.Max(1, (int)Math.Round(win.Width * k)), h = Math.Max(1, (int)Math.Round(win.Height * k));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(s.Background), null, new Rect(0, 0, w + 2 * margin, h + 2 * margin));
            foreach (var r in Placements(s, new Int32Rect(win.X - margin * 4, win.Y - margin * 4, win.Width + margin * 8, win.Height + margin * 8)))
                dc.DrawImage(s.Image, new Rect((r.X - win.X) * k + margin, (r.Y - win.Y) * k + margin, r.Width * k, r.Height * k));
        }
        if (blurPx > 0)
            dv.Effect = new BlurEffect { Radius = blurPx * k, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };

        var rtb = new RenderTargetBitmap(w + 2 * margin, h + 2 * margin, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        // copy out, so the bigger render target (with blur margins) can be freed
        var copy = new WriteableBitmap(new CroppedBitmap(rtb, new Int32Rect(margin, margin, w, h)));
        copy.Freeze();
        return copy;
    }

    /// <summary>A vivid colour taken from the wallpaper behind the given area (Todowall-style auto accent).</summary>
    public static Color? Accent(Source s, Int32Rect area)
    {
        var small = Render(s, area, 0, 0.05);
        if (small == null) return null;
        var bmp = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);

        var weight = new double[12];
        var sum = new double[12, 3];
        for (int i = 0; i < px.Length; i += 16) // sample every 4th pixel
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            ToHsv(r, g, b, out var hue, out var sat, out var val);
            double wgt = sat * sat * val;
            if (wgt < 0.02) continue;
            int bucket = (int)(hue / 30) % 12;
            weight[bucket] += wgt;
            sum[bucket, 0] += r * wgt; sum[bucket, 1] += g * wgt; sum[bucket, 2] += b * wgt;
        }
        int best = Array.IndexOf(weight, weight.Max());
        if (weight[best] < 1) return null; // practically grey wallpaper
        ToHsv(sum[best, 0] / weight[best], sum[best, 1] / weight[best], sum[best, 2] / weight[best], out var h2, out var s2, out _);
        return FromHsv(h2, Math.Clamp(s2 * 1.25, 0.5, 0.85), 0.96);
    }

    static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) { h = 0; return; }
        h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
