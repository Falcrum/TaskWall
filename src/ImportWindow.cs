using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskWall;

/// <summary>Normal (non-desktop) dark window used for dialogs.</summary>
public class DarkWindow : Window
{
    public DarkWindow()
    {
        Background = Ui.Res("DialogBg");
        Foreground = Ui.Res("Fg");
        FontFamily = Ui.Font("UiFont");
        FontSize = 12.5;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    protected static TextBlock Label(string text, double size = 12.5, string brush = "Fg", FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Ui.Res(brush),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    protected static Button Btn(string text, string style, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[style], Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }
}

public sealed class ImportWindow : DarkWindow
{
    sealed class Row
    {
        public required string[] Raw;
        public string Title = "";
        public string? Status, Priority, Id, Url;
        public double? Estimate;
        public TaskItem? Existing;
        public bool Visible = true;
        public CheckBox Box = null!;
        public FrameworkElement View = null!;
    }

    static readonly string None = L.T("(brak)");
    static readonly string ZipNames = L.T("(z nazw plików w ZIP)");
    static readonly string NoStatus = L.T("(bez statusu)");

    readonly NotionExport _x;
    readonly ComboBox _title = new(), _status = new(), _prio = new(), _est = new(), _link = new();
    readonly TextBox _search = new();
    readonly WrapPanel _chips = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _list = new();
    readonly Border _linkInfo = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Button _importBtn;
    readonly HashSet<string> _statusOff = new();
    List<Row> _rows = new();

    public int Imported { get; private set; }
    public int Updated { get; private set; }

    public ImportWindow(NotionExport export)
    {
        _x = export;
        Title = L.T("Import z Notion");
        Width = 820;
        Height = 720;
        MinWidth = 600;
        MinHeight = 480;

        var t = export.Table;
        var cols = new List<string> { None };
        cols.AddRange(t.Headers.Select((h, i) => string.IsNullOrWhiteSpace(h) ? L.F("Kolumna {0}", i + 1) : h));
        foreach (var cb in new[] { _title, _status, _prio, _est }) cb.ItemsSource = cols;
        var linkCols = new List<string>(cols);
        if (export.HasZipIds) linkCols.Insert(1, ZipNames);
        _link.ItemsSource = linkCols;

        _title.SelectedIndex = NotionImport.DetectTitle(t) + 1;
        _status.SelectedIndex = NotionImport.DetectStatus(t) + 1;
        _prio.SelectedIndex = NotionImport.DetectPriority(t) + 1;
        _est.SelectedIndex = NotionImport.DetectEstimate(t) + 1;
        int link = NotionImport.DetectLink(t);
        _link.SelectedItem = link >= 0 ? cols[link + 1] : export.HasZipIds ? ZipNames : None;

        foreach (var cb in new[] { _title, _status, _prio, _est, _link })
            cb.SelectionChanged += (_, _) => { if (IsLoaded) BuildRows(); };

        // ----- layout -----
        var dock = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = dock;

        var head = new StackPanel();
        DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(Label(L.T("Import z Notion do backlogu"), 18, "Fg", FontWeights.SemiBold));
        head.Children.Add(Label(L.F("{0} · {1} wierszy", export.SourceName, t.Rows.Count), 12, "FgFaint"));
        dock.Children.Add(head);

        var map = new UniformGrid { Columns = 5, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(map, Dock.Top);
        map.Children.Add(Field(L.T("Nazwa zadania"), _title));
        map.Children.Add(Field("Status", _status));
        map.Children.Add(Field(L.T("Priorytet"), _prio));
        map.Children.Add(Field(L.T("Estymacja (h)"), _est));
        map.Children.Add(Field(L.T("Link do strony"), _link));
        dock.Children.Add(map);

        DockPanel.SetDock(_linkInfo, Dock.Top);
        dock.Children.Add(_linkInfo);

        var filter = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(filter, Dock.Top);
        var selAll = Btn(L.T("Zaznacz widoczne"), "LinkButton", (_, _) => SetVisibleChecked(true));
        var selNone = Btn(L.T("Odznacz widoczne"), "LinkButton", (_, _) => SetVisibleChecked(false));
        DockPanel.SetDock(selNone, Dock.Right);
        DockPanel.SetDock(selAll, Dock.Right);
        filter.Children.Add(selNone);
        filter.Children.Add(selAll);
        var searchHost = new Grid();
        _search.Style = (Style)Application.Current.Resources["FieldBox"];
        _search.Padding = new Thickness(24, 4, 6, 4);
        _search.TextChanged += (_, _) => ApplyFilter();
        var hint = Label(L.T("Filtruj po nazwie…"), 12.5, "FgFaint");
        hint.Margin = new Thickness(28, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchHost.Children.Add(_search);
        searchHost.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Font("IconFont"), FontSize = 11, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        searchHost.Children.Add(hint);
        filter.Children.Add(searchHost);
        dock.Children.Add(filter);

        DockPanel.SetDock(_chips, Dock.Top);
        dock.Children.Add(_chips);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        _importBtn = Btn(L.T("Importuj"), "PrimaryButton", (_, _) => DoImport());
        var cancel = Btn(L.T("Anuluj"), "SecondaryButton", (_, _) => { DialogResult = false; });
        DockPanel.SetDock(_importBtn, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(_importBtn);
        footer.Children.Add(cancel);
        _summary.Foreground = Ui.Res("FgDim");
        footer.Children.Add(_summary);
        dock.Children.Add(footer);

        var listHost = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = Ui.Res("CardBg"),
            Padding = new Thickness(4),
            Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        dock.Children.Add(listHost);

        Loaded += (_, _) => BuildRows();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    static FrameworkElement Field(string label, ComboBox combo)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var l = Label(label, 11.5, "FgDim");
        l.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(l);
        sp.Children.Add(combo);
        return sp;
    }

    /// <summary>Header index picked in a combo (items: "(brak)", [zip option], headers…), or -1.</summary>
    int Col(ComboBox cb)
    {
        int offset = cb == _link && _x.HasZipIds ? 2 : 1;
        return cb.SelectedIndex < offset ? -1 : cb.SelectedIndex - offset;
    }

    void BuildRows()
    {
        var t = _x.Table;
        int ti = Col(_title), si = Col(_status), pi = Col(_prio), ei = Col(_est), li = Col(_link);
        bool fromZip = (string?)_link.SelectedItem == ZipNames;
        var tasks = App.Store.Data.Tasks;

        _rows = new List<Row>();
        foreach (var raw in t.Rows)
        {
            var title = ti >= 0 ? t.Cell(raw, ti) : "";
            if (title.Length == 0) continue;
            var r = new Row
            {
                Raw = raw,
                Title = title,
                Status = si >= 0 ? NullIfEmpty(BoardWindow.CleanLabel(t.Cell(raw, si))) : null,
                Priority = pi >= 0 ? NullIfEmpty(BoardWindow.CleanLabel(t.Cell(raw, pi))) : null,
                Estimate = ei >= 0 ? NotionImport.ParseHours(t.Cell(raw, ei)) : null,
            };
            if (fromZip) r.Id = NotionImport.IdFromZip(_x, title);
            else if (li >= 0)
            {
                var v = t.Cell(raw, li);
                r.Id = NotionImport.ExtractId(v);
                if (r.Id == null && v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) r.Url = v;
            }
            if (r.Id != null) r.Url = NotionImport.PageUrl(r.Id);
            // by page id; otherwise (or e.g. after adding the link formula later) by title among tasks without a link
            r.Existing = (r.Id != null ? tasks.FirstOrDefault(x => x.NotionId == r.Id) : null)
                ?? tasks.FirstOrDefault(x => x.NotionId == null && string.Equals(x.Text, title, StringComparison.OrdinalIgnoreCase));
            _rows.Add(r);
        }

        // status chips (statuses that look finished start switched off)
        _statusOff.Clear();
        _chips.Children.Clear();
        var statuses = _rows.Select(r => r.Status ?? NoStatus).Distinct().ToList();
        if (si >= 0 && statuses.Count > 1)
        {
            foreach (var s in statuses)
            {
                int count = _rows.Count(r => (r.Status ?? NoStatus) == s);
                bool on = !NotionImport.LooksDone(s);
                if (!on) _statusOff.Add(s);
                var chip = new ToggleButton { Style = (Style)Application.Current.Resources["Chip"], IsChecked = on, Content = $"{s}  {count}" };
                chip.Checked += (_, _) => { _statusOff.Remove(s); ApplyFilter(); };
                chip.Unchecked += (_, _) => { _statusOff.Add(s); ApplyFilter(); };
                _chips.Children.Add(chip);
            }
        }

        _list.Children.Clear();
        foreach (var r in _rows)
        {
            r.View = BuildRowView(r);
            _list.Children.Add(r.View);
        }

        UpdateLinkInfo(li >= 0 || fromZip);
        ApplyFilter();
    }

    FrameworkElement BuildRowView(Row r)
    {
        var bd = new Border { Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        r.Box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
        r.Box.Checked += (_, _) => UpdateSummary();
        r.Box.Unchecked += (_, _) => UpdateSummary();
        g.Children.Add(r.Box);

        var sp = new StackPanel();
        Grid.SetColumn(sp, 1);
        sp.Children.Add(Label(r.Title));
        var meta = string.Join("  ·  ", new[] { r.Status, r.Priority, r.Estimate is > 0 ? r.Estimate.Value.ToString("0.#", BoardWindow.Pl) + "h" : null }.Where(s => !string.IsNullOrEmpty(s)));
        if (meta.Length > 0) sp.Children.Add(Label(meta, 11, "FgFaint"));
        g.Children.Add(sp);

        var (badge, color) = r.Existing != null ? (L.T("AKTUALIZACJA"), Color.FromRgb(0x4A, 0x50, 0x5E))
            : r.Url == null ? (L.T("BEZ LINKU"), Color.FromRgb(0x8A, 0x5A, 0x1E))
            : (L.T("NOWE"), ((SolidColorBrush)Ui.Res("AccentBrush")).Color);
        var pill = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 1, 0, 0),
            Child = new TextBlock { Text = badge, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
        };
        if (r.Existing != null) pill.ToolTip = r.Existing.Archived ? L.T("Jest w archiwum – zostanie zaktualizowane (zostaje w archiwum)")
            : r.Existing.IsBacklog ? L.T("Jest już w backlogu – zostanie zaktualizowane") : L.F("Jest już na tablicy ({0}) – zostanie zaktualizowane w miejscu", r.Existing.Day);
        Grid.SetColumn(pill, 2);
        g.Children.Add(pill);

        bd.Child = g;
        bd.MouseEnter += (_, _) => bd.Background = Ui.Res("Hover");
        bd.MouseLeave += (_, _) => bd.Background = Brushes.Transparent;
        bd.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) r.Box.IsChecked = !r.Box.IsChecked; };
        return bd;
    }

    void UpdateLinkInfo(bool hasLinks)
    {
        _linkInfo.Child = null;
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        int withLink = _rows.Count(r => r.Url != null);
        if (hasLinks && withLink > 0)
        {
            _linkInfo.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0xC3, 0x8A));
            tb.Inlines.Add(new Run(L.F("✓ Linki do stron Notion: {0} z {1}. Kliknięcie zadania na tablicy otworzy jego stronę.", withLink, _rows.Count)));
        }
        else
        {
            _linkInfo.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xF0, 0xA0, 0x4B));
            tb.Inlines.Add(new Run(L.T("Eksport CSV z Notion nie zawiera linków do stron. ")) { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(L.T("Żeby zadania były linkami, dodaj w bazie Notion właściwość typu Formula o tej treści (kolumna pojawi się w CSV):")));
            _linkInfo.Child = new StackPanel
            {
                Children =
                {
                    tb,
                    new TextBox
                    {
                        Text = NotionImport.Formula, IsReadOnly = true, Margin = new Thickness(0, 6, 0, 4),
                        Style = (Style)Application.Current.Resources["FieldBox"], FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    },
                    Label(L.T("Możesz też wyeksportować bazę jako ZIP z opcją „Include subpages” i wczytać cały plik ZIP. Wtedy linki zostaną odtworzone z nazw plików."), 11.5, "FgDim"),
                },
            };
            return;
        }
        _linkInfo.Child = tb;
    }

    void ApplyFilter()
    {
        var q = _search.Text.Trim();
        foreach (var r in _rows)
        {
            r.Visible = !_statusOff.Contains(r.Status ?? NoStatus) &&
                        (q.Length == 0 || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
            r.View.Visibility = r.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSummary();
    }

    void SetVisibleChecked(bool on)
    {
        foreach (var r in _rows.Where(r => r.Visible)) r.Box.IsChecked = on;
    }

    IEnumerable<Row> Selected => _rows.Where(r => r.Visible && r.Box.IsChecked == true);

    void UpdateSummary()
    {
        var sel = Selected.ToList();
        int upd = sel.Count(r => r.Existing != null);
        _summary.Text = L.F("Wybrane: {0} z {1} widocznych   ·   nowe: {2}   ·   aktualizacje: {3}", sel.Count, _rows.Count(r => r.Visible), sel.Count - upd, upd);
        _importBtn.IsEnabled = sel.Count > 0;
        _importBtn.Content = sel.Count > 0 ? L.F("Importuj ({0})", sel.Count) : L.T("Importuj");
    }

    void DoImport()
    {
        var store = App.Store;
        store.Checkpoint();
        var backlog = store.Data.Tasks.Where(t => t.IsBacklog).ToList();
        double order = backlog.Count > 0 ? backlog.Max(t => t.Order) + 1 : 0;
        var addedNow = new Dictionary<string, TaskItem>(); // the same page twice in one file → one task
        foreach (var r in Selected)
        {
            // a sync may have replaced objects while the dialog was open: always work on the live instance
            var existing = (r.Existing != null ? store.Find(r.Existing.Id) : null)
                ?? (r.Id != null && addedNow.TryGetValue(r.Id, out var dup) ? dup : null);
            if (existing != null)
            {
                var e = existing;
                if (e.Text != r.Title || e.Status != r.Status || e.Priority != r.Priority || (r.Estimate != null && e.Estimate != r.Estimate) || (r.Url != null && e.Url != r.Url))
                {
                    e.Text = r.Title;
                    e.Status = r.Status;
                    e.Priority = r.Priority;
                    if (r.Estimate != null) e.Estimate = r.Estimate;
                    if (r.Url != null) { e.Url = r.Url; e.NotionId = r.Id; }
                    store.Changed(e);
                }
                Updated++;
            }
            else
            {
                var created = new TaskItem
                {
                    Text = r.Title,
                    Status = r.Status,
                    Priority = r.Priority,
                    Estimate = r.Estimate,
                    NotionId = r.Id,
                    Url = r.Url,
                    Day = null,
                    Order = order++,
                };
                store.Add(created);
                if (r.Id != null) addedNow[r.Id] = created;
                Imported++;
            }
        }
        store.SaveNow();
        DialogResult = true;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
