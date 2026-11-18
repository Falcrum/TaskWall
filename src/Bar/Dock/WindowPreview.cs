using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// Live previews of a dock icon's windows over (or under) the bar: one tile per window with its title, a close button and the
/// window itself drawn live by DWM (DwmRegisterThumbnail – which needs a window that is not per-pixel transparent, so this one is
/// opaque). A click on a tile brings that window up.
/// </summary>
sealed class WindowPreview : GlassWindow
{
    const double TileW = 230, ThumbH = 132, TitleH = 24, Pad = 10, Gap = 10;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);

    readonly Canvas _tiles = new();
    readonly List<IntPtr> _thumbs = new();
    List<TaskList.Task> _shown = new();

    /// <summary>A tile was clicked: bring that window up.</summary>
    public event Action<IntPtr>? Picked;
    /// <summary>A window's close button.</summary>
    public event Action<IntPtr>? CloseRequested;

    public WindowPreview()
    {
        MakeOpaque();
        Radius = 12;
        StaysOnTop = true;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        Body = _tiles;
        SetResourceReference(BackgroundProperty, "MenuBg");
        Closed += (_, _) => Clear();
    }

    public IReadOnlyList<TaskList.Task> Shown => _shown;
    public int ThumbCount => _thumbs.Count;

    /// <summary>
    /// Shows <paramref name="windows"/> centred on <paramref name="iconCentreX"/> (physical px), right above the bar's
    /// <paramref name="strip"/> (under it when the bar is at the top), inside the strip's width.
    /// </summary>
    public void Present(IReadOnlyList<TaskList.Task> windows, double iconCentreX, PxRect strip, bool barTop, double scale)
    {
        Clear();
        _shown = windows.Take(Math.Max(1, (int)((strip.Width / scale - 2 * Pad) / (TileW + Gap)))).ToList();
        if (_shown.Count == 0) { Hide(); return; }
        double w = 2 * Pad + _shown.Count * TileW + (_shown.Count - 1) * Gap, h = 2 * Pad + ThumbH + TitleH;
        _tiles.Children.Clear();
        _tiles.Width = w;
        _tiles.Height = h;
        for (int i = 0; i < _shown.Count; i++)
        {
            var t = _shown[i];
            var inner = new Grid();
            var tile = new Border { Width = TileW, Height = ThumbH + TitleH, CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Child = inner };
            Anim.HoverBackground(tile, Colors.Transparent, Th.Ink(0x16));
            var title = new TextBlock { Text = t.Title, FontSize = 12, Margin = new Thickness(8, 4, 28, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top, Foreground = Ui.Res("Fg") };
            inner.Children.Add(title);
            var close = new Border
            {
                Width = 22, Height = 20, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0), ToolTip = L.T("Zamknij okno"),
                Child = new TextBlock { Text = "✕", FontSize = 11, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgDim") },
            };
            Anim.HoverBackground(close, Colors.Transparent, Color.FromArgb(0xC0, 0xE8, 0x48, 0x48));
            var hwnd = t.Hwnd;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseRequested?.Invoke(hwnd); };
            inner.Children.Add(close);
            tile.MouseLeftButtonUp += (_, _) => Picked?.Invoke(hwnd);
            Canvas.SetLeft(tile, Pad + i * (TileW + Gap));
            Canvas.SetTop(tile, Pad);
            _tiles.Children.Add(tile);
        }
        int pw = (int)Math.Round(w * scale), ph = (int)Math.Round(h * scale), gap = (int)Math.Round(8 * scale);
        int x = (int)Math.Round(iconCentreX - pw / 2.0);
        x = Math.Clamp(x, strip.X + gap, Math.Max(strip.X + gap, strip.Right - pw - gap));
        int y = barTop ? strip.Bottom + gap : strip.Y - ph - gap;
        PlacePhysical(x, y, pw, ph, scale);
        App.Instance.PaintGlass(this); // the frosted glass of the bar under the live pictures
        if (!IsVisible) Show();
        // the live pictures: DWM draws each window into its tile (physical pixels of this window's client area)
        for (int i = 0; i < _shown.Count; i++)
        {
            if (DwmRegisterThumbnail(Hwnd, _shown[i].Hwnd, out var thumb) != 0) continue;
            _thumbs.Add(thumb);
            DwmQueryThumbnailSourceSize(thumb, out var src);
            double boxL = (Pad + i * (TileW + Gap) + 6) * scale, boxT = (Pad + TitleH) * scale, boxW = (TileW - 12) * scale, boxH = (ThumbH - 6) * scale;
            double k = src.X > 0 && src.Y > 0 ? Math.Min(boxW / src.X, boxH / src.Y) : 1;
            double tw = src.X > 0 ? src.X * k : boxW, th = src.Y > 0 ? src.Y * k : boxH;
            int l = (int)Math.Round(boxL + (boxW - tw) / 2), tp = (int)Math.Round(boxT + (boxH - th) / 2);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8 | 0x10, // destination, opacity, visible, the whole window (not only the client area)
                rcDestination = new RECT { Left = l, Top = tp, Right = l + (int)Math.Round(tw), Bottom = tp + (int)Math.Round(th) },
                opacity = 255, fVisible = 1, fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void Clear()
    {
        foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
        _thumbs.Clear();
    }

    /// <summary>Hidden (the live pictures let go: DWM stops drawing them).</summary>
    public void Dismiss()
    {
        Clear();
        _shown = new();
        if (IsVisible) Hide();
    }
}
