using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskWall;

/// <summary>
/// Icons and names of programs for the dock, from the shell: a program file or a Store app (shell:AppsFolder\&lt;AppUserModelID&gt;),
/// in 128 px with their transparency (sharp at the magnified size on a 150 % screen); a window's own icon when the program can't
/// be read. Cached per program.
/// </summary>
static class ShellIcons
{
    const int Size = 128;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr h, int index);

    static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    static string Parsing(string? exe, string? appId) => !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : !string.IsNullOrEmpty(appId) ? @"shell:AppsFolder\" + appId : exe ?? "";

    static IShellItem? Item(string parsing)
    {
        if (parsing.Length == 0) return null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref iid, out var item);
            return item;
        }
        catch { return null; }
    }

    /// <summary>The program's icon (a Store app by its id; the window's own icon when neither can be read).</summary>
    public static ImageSource? Icon(string key, string? exe, string? appId, IntPtr window)
    {
        if (Icons.TryGetValue(key, out var known)) return known; // none too: a program without an icon isn't asked again on every refresh
        ImageSource? img = null;
        // a Store app's own tile icon beats ApplicationFrameHost.exe's
        foreach (var parsing in new[] { !string.IsNullOrEmpty(appId) ? @"shell:AppsFolder\" + appId : "", Parsing(exe, null) })
        {
            if (img != null || Item(parsing) is not { } item) continue;
            try
            {
                if (item is IShellItemImageFactory f && f.GetImage(new SIZE { cx = Size, cy = Size }, 0x1 | 0x4 /* BIGGERSIZEOK | ICONONLY */, out var hbmp) == 0 && hbmp != IntPtr.Zero)
                {
                    img = FromHBitmap(hbmp);
                    Native.DeleteObject(hbmp);
                }
            }
            catch { }
            finally { Marshal.ReleaseComObject(item); }
        }
        img ??= WindowIcon(window);
        Icons[key] = img;
        return img;
    }

    /// <summary>The program's name as the Start menu shows it ("Blender", "Terminal"), else the file's description or name.</summary>
    public static string Name(string key, string? exe, string? appId, string fallback)
    {
        if (Names.TryGetValue(key, out var n)) return n;
        string name = "";
        if (!string.IsNullOrEmpty(appId) && Item(@"shell:AppsFolder\" + appId) is { } item)
        {
            try { item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out name); } catch { }
            finally { Marshal.ReleaseComObject(item); }
        }
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
        }
        if (string.IsNullOrWhiteSpace(name)) name = fallback;
        return Names[key] = name.Trim();
    }

    /// <summary>A 32-bit shell bitmap (premultiplied alpha) → an image with its transparency (CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbmp, 0, (uint)h, bits, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // icons without an alpha channel come back with alpha 0 everywhere: then every pixel is opaque
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        src.Freeze();
        return src;
    }

    /// <summary>The window's big icon (WM_GETICON, else its class icon).</summary>
    static ImageSource? WindowIcon(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        try
        {
            SendMessageTimeout(window, 0x007F /* WM_GETICON */, new IntPtr(1) /* ICON_BIG */, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 100, out var icon);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, -14 /* GCLP_HICON */);
            if (icon == IntPtr.Zero) return null;
            var img = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Starts the program (a new window): the file with its arguments, or a Store app by its id.</summary>
    public static void Launch(string? exe, string? args, string? appId, bool admin = false)
    {
        try
        {
            if (HasExe(exe))
                Process.Start(new ProcessStartInfo(exe!, args ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "", Verb = admin ? "runas" : "" });
            else if (!string.IsNullOrEmpty(appId))
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + appId) { UseShellExecute = true }); // a Store app can't be elevated: started normally
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // the UAC prompt cancelled: not an error
        catch (Exception ex) { Log.Error("dock launch " + (exe ?? appId), ex); }
    }

    static bool HasExe(string? exe) =>
        !string.IsNullOrEmpty(exe) && File.Exists(exe) && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the dropped files with the program: its file gets them as quoted arguments, a Store app has each opened by the shell's default handler.</summary>
    public static void OpenFiles(string? exe, string? appId, string[] files)
    {
        try
        {
            if (HasExe(exe))
                Process.Start(new ProcessStartInfo(exe!, string.Join(" ", files.Select(f => "\"" + f + "\""))) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            else
                foreach (var f in files) Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("dock open files " + (exe ?? appId), ex); }
    }
}
