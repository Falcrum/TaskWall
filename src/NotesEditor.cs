using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// The day's notebook (next to the day view's timeline), a little like a Notion page: headings, bold / italic /
/// strike, bullet and numbered lists, check boxes, quotes and a divider. Typing "# ", "## ", "- ", "1. ", "[] ", "> "
/// or "---" at the start of a line formats it. Kept as light Markdown, so it syncs as plain text and stays readable.
/// </summary>
public sealed class NotesEditor : DockPanel
{
    readonly RichTextBox _box = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), AcceptsTab = true, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 4, 4, 4),
    };
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _loading;
    string _last = "";
    double Base => App.Settings.FontSize;

    /// <summary>The text changed (Markdown), a moment after the last key.</summary>
    public event Action<string>? Changed;
    public bool Editing => _box.IsKeyboardFocusWithin;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public NotesEditor()
    {
        _box.Foreground = Res("Fg");
        _box.CaretBrush = Res("Fg");
        _box.SelectionBrush = Res("AccentBrush");
        _box.Document.PagePadding = new Thickness(0);
        _box.Document.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _box.Document.FontSize = Base;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Button Tool(string text, string tip, Action a, FontWeight? weight = null, FontStyle? style = null, TextDecorationCollection? deco = null)
        {
            var tb = new TextBlock { Text = text, FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 12, FontWeight = weight ?? FontWeights.SemiBold, FontStyle = style ?? FontStyles.Normal, TextDecorations = deco };
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = tb, ToolTip = tip, Padding = new Thickness(7, 2, 7, 3), Focusable = false };
            b.Click += (_, _) => { a(); _box.Focus(); };
            bar.Children.Add(b);
            return b;
        }
        Tool("H1", L.T("Nagłówek 1  (# spacja)"), () => SetBlock("h1"));
        Tool("H2", L.T("Nagłówek 2  (## spacja)"), () => SetBlock("h2"));
        Tool("H3", L.T("Nagłówek 3  (### spacja)"), () => SetBlock("h3"));
        Tool("¶", L.T("Zwykły tekst"), () => SetBlock(null));
        bar.Children.Add(new Border { Width = 1, Height = 14, Margin = new Thickness(4, 0, 4, 0), Background = Res("Line") });
        Tool("B", L.T("Pogrubienie  (Ctrl+B)"), () => EditingCommands.ToggleBold.Execute(null, _box), FontWeights.Bold);
        Tool("I", L.T("Kursywa  (Ctrl+I)"), () => EditingCommands.ToggleItalic.Execute(null, _box), FontWeights.Normal, FontStyles.Italic);
        Tool("S", L.T("Przekreślenie  (Ctrl+Shift+S)"), ToggleStrike, FontWeights.Normal, null, TextDecorations.Strikethrough);
        bar.Children.Add(new Border { Width = 1, Height = 14, Margin = new Thickness(4, 0, 4, 0), Background = Res("Line") });
        Tool("•", L.T("Lista  (- spacja)"), () => EditingCommands.ToggleBullets.Execute(null, _box));
        Tool("1.", L.T("Lista numerowana  (1. spacja)"), () => EditingCommands.ToggleNumbering.Execute(null, _box));
        Tool("☐", L.T("Pole wyboru  ([] spacja)"), ToggleTodo);
        Tool("❝", L.T("Cytat  (> spacja)"), () => SetBlock("quote"));
        Tool("—", L.T("Linia oddzielająca  (---)"), InsertDivider);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var area = new Grid();
        area.Children.Add(_box);
        var hint = new TextBlock { Text = L.T("Pisz tutaj…  # nagłówek, - lista, [] pole wyboru, **pogrubienie**"), Foreground = Res("FgFaint"), FontSize = Base - 0.5, Margin = new Thickness(2, 6, 0, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
        area.Children.Add(hint);
        void Hint() => hint.Visibility = new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd).Text.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _box.TextChanged += (_, _) => Hint();
        Children.Add(area);

        _box.TextChanged += (_, _) => { if (!_loading) { _save.Stop(); _save.Start(); } };
        _save.Tick += (_, _) => { _save.Stop(); Flush(); };
        _box.PreviewKeyDown += OnKey;
        _box.LostKeyboardFocus += (_, _) => Flush();
    }

    /// <summary>Shows a note (Markdown). Ignored while the user types into it (their version wins and is saved).</summary>
    public void Load(string markdown, bool force = false)
    {
        if (!force && (Editing || markdown == _last)) return;
        _loading = true;
        _box.Document.Blocks.Clear();
        foreach (var b in Parse(markdown)) _box.Document.Blocks.Add(b);
        if (_box.Document.Blocks.Count == 0) _box.Document.Blocks.Add(new Paragraph());
        _last = markdown;
        _loading = false;
    }

    void Flush()
    {
        if (_loading) return;
        var md = Serialize();
        if (md == _last) return;
        _last = md;
        Changed?.Invoke(md);
    }

    // ---------- blocks ----------

    Paragraph? Current => _box.CaretPosition.Paragraph;

    void StyleBlock(Paragraph p, string? kind)
    {
        p.Tag = kind;
        p.ClearValue(Block.FontSizeProperty);
        p.ClearValue(Block.FontWeightProperty);
        p.ClearValue(Block.ForegroundProperty);
        p.ClearValue(Block.BorderThicknessProperty);
        p.ClearValue(Block.BorderBrushProperty);
        p.ClearValue(Block.PaddingProperty);
        p.ClearValue(TextElement.FontStyleProperty);
        p.Margin = new Thickness(0, 2, 0, 2);
        switch (kind)
        {
            case "h1": p.FontSize = Base * 1.6; p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 10, 0, 4); break;
            case "h2": p.FontSize = Base * 1.32; p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 8, 0, 3); break;
            case "h3": p.FontSize = Base * 1.12; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 6, 0, 2); break;
            case "quote":
                p.BorderBrush = Res("AccentBrush"); p.BorderThickness = new Thickness(2.5, 0, 0, 0); p.Padding = new Thickness(10, 1, 0, 1);
                p.Foreground = Res("FgDim"); break;
            case "hr": p.BorderBrush = Res("Line"); p.BorderThickness = new Thickness(0, 0, 0, 1); p.Margin = new Thickness(0, 6, 0, 8); p.FontSize = 2; break;
        }
    }

    void SetBlock(string? kind)
    {
        if (Current is not { } p) return;
        if (p.Tag as string == "todo") RemoveCheck(p);
        StyleBlock(p, p.Tag as string == kind ? null : kind);
    }

    void InsertDivider()
    {
        if (Current is not { } p) return;
        var hr = new Paragraph();
        StyleBlock(hr, "hr");
        var after = new Paragraph();
        StyleBlock(after, null);
        if (p.Parent is FlowDocument doc) { doc.Blocks.InsertAfter(p, hr); doc.Blocks.InsertAfter(hr, after); }
        _box.CaretPosition = after.ContentStart;
    }

    void ToggleStrike()
    {
        var sel = _box.Selection;
        var cur = sel.GetPropertyValue(Inline.TextDecorationsProperty);
        bool on = cur is TextDecorationCollection c && c.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, on ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    // check box lines
    void ToggleTodo()
    {
        if (Current is not { } p) return;
        if (p.Tag as string == "todo") { RemoveCheck(p); StyleBlock(p, null); }
        else MakeTodo(p, false);
    }

    void MakeTodo(Paragraph p, bool done)
    {
        StyleBlock(p, "todo");
        var cb = new CheckBox { IsChecked = done, Margin = new Thickness(0, 0, 6, -2), Cursor = Cursors.Hand, Focusable = false };
        cb.Checked += (_, _) => { DimTodo(p, true); Flush(); };
        cb.Unchecked += (_, _) => { DimTodo(p, false); Flush(); };
        var holder = new InlineUIContainer(cb) { BaselineAlignment = BaselineAlignment.Center };
        if (p.Inlines.FirstInline is { } first) p.Inlines.InsertBefore(first, holder); else p.Inlines.Add(holder);
        DimTodo(p, done);
    }

    static void DimTodo(Paragraph p, bool done)
    {
        foreach (var run in p.Inlines.OfType<Run>())
        {
            run.TextDecorations = done ? TextDecorations.Strikethrough : null;
            if (done) run.Foreground = Res("FgFaint"); else run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    static void RemoveCheck(Paragraph p)
    {
        if (p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox }) p.Inlines.Remove(p.Inlines.FirstInline);
        DimTodo(p, false);
    }

    static bool IsTodoChecked(Paragraph p) => p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox { IsChecked: true } };

    void OnKey(object sender, KeyEventArgs e)
    {
        // shortcuts of the notebook (they don't reach the board: Ctrl+1 / 2 etc. stay here)
        if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; ToggleStrike(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3)
        {
            e.Handled = true;
            SetBlock(e.Key == Key.D1 ? "h1" : e.Key == Key.D2 ? "h2" : "h3");
            return;
        }
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Current is { } p && p.Parent is FlowDocument)
        {
            // "# " "## " "### " "- " "* " "1. " "[] " "> " at the start of a line
            var before = new TextRange(p.ContentStart, _box.CaretPosition).Text;
            string? kind = before switch { "#" => "h1", "##" => "h2", "###" => "h3", ">" => "quote", "-" or "*" => "bullet", "1." => "number", "[]" or "[ ]" => "todo", _ => null };
            if (kind == null) return;
            e.Handled = true;
            new TextRange(p.ContentStart, _box.CaretPosition).Text = "";
            if (kind == "bullet") EditingCommands.ToggleBullets.Execute(null, _box);
            else if (kind == "number") EditingCommands.ToggleNumbering.Execute(null, _box);
            else if (kind == "todo") MakeTodo(p, false);
            else StyleBlock(p, kind);
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Current is { } cur)
        {
            var kind = cur.Tag as string;
            var text = new TextRange(cur.ContentStart, cur.ContentEnd).Text.Trim();
            if (kind == "todo" && text.Length == 0) { e.Handled = true; RemoveCheck(cur); StyleBlock(cur, null); return; } // empty line ends the list
            if (text == "---" && cur.Parent is FlowDocument) { e.Handled = true; new TextRange(cur.ContentStart, cur.ContentEnd).Text = ""; StyleBlock(cur, "hr"); var next = new Paragraph(); StyleBlock(next, null); ((FlowDocument)cur.Parent).Blocks.InsertAfter(cur, next); _box.CaretPosition = next.ContentStart; return; }
            // after the split: a heading is followed by plain text, a check box line by another check box line
            Dispatcher.BeginInvoke(() =>
            {
                if (Current is not { } np || ReferenceEquals(np, cur)) return;
                if (kind is "h1" or "h2" or "h3" or "quote" or "hr") StyleBlock(np, null);
                else if (kind == "todo") { RemoveCheck(np); if (np.Inlines.FirstInline is InlineUIContainer) np.Inlines.Remove(np.Inlines.FirstInline); MakeTodo(np, false); }
            }, DispatcherPriority.Input);
        }
    }

    // ---------- Markdown out ----------

    string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var b in _box.Document.Blocks) Write(sb, b, 0);
        return sb.ToString().TrimEnd('\n', '\r', ' ');
    }

    static void Write(StringBuilder sb, Block b, int depth)
    {
        switch (b)
        {
            case Paragraph p:
                var kind = p.Tag as string;
                var text = Inlines(p.Inlines);
                sb.Append(kind switch
                {
                    "h1" => "# " + text, "h2" => "## " + text, "h3" => "### " + text, "quote" => "> " + text, "hr" => "---",
                    "todo" => (IsTodoChecked(p) ? "- [x] " : "- [ ] ") + text,
                    _ => text,
                }).Append('\n');
                break;
            case List list:
                int n = 1;
                foreach (var item in list.ListItems)
                {
                    bool first = true;
                    foreach (var inner in item.Blocks)
                    {
                        if (inner is List nested) { Write(sb, nested, depth + 1); continue; }
                        var marker = list.MarkerStyle == TextMarkerStyle.Decimal ? $"{n}. " : "- ";
                        sb.Append(new string(' ', depth * 2)).Append(first ? marker : new string(' ', marker.Length));
                        sb.Append(inner is Paragraph ip ? Inlines(ip.Inlines) : "").Append('\n');
                        first = false;
                    }
                    n++;
                }
                break;
        }
    }

    static string Inlines(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (var run in Runs(inlines))
        {
            if (run.Text.Length == 0) continue;
            bool bold = run.FontWeight >= FontWeights.SemiBold && !(run.Parent is Paragraph { Tag: "h1" or "h2" or "h3" });
            bool italic = run.FontStyle == FontStyles.Italic;
            bool strike = run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true && !(FindParagraph(run) is { Tag: "todo" });
            var t = run.Text;
            if (strike) t = "~~" + t + "~~";
            if (italic) t = "*" + t + "*";
            if (bold) t = "**" + t + "**";
            sb.Append(t);
        }
        return sb.ToString().Replace("****", "").Replace("~~~~", "");
    }

    static Paragraph? FindParagraph(TextElement el)
    {
        for (DependencyObject? d = el; d != null; d = (d as TextElement)?.Parent) if (d is Paragraph p) return p;
        return null;
    }

    static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Run r) yield return r;
            else if (i is Span s) foreach (var x in Runs(s.Inlines)) yield return x;
            else if (i is LineBreak) yield return new Run(" ");
        }
    }

    // ---------- Markdown in ----------

    static readonly Regex ListLine = new(@"^(\s*)(-|\*|\d+\.)\s+(.*)$");
    static readonly Regex TodoLine = new(@"^\s*[-*]\s+\[( |x|X)\]\s?(.*)$");

    IEnumerable<Block> Parse(string md)
    {
        var blocks = new List<Block>();
        var lists = new List<List>(); // the open list per depth
        foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            Match m;
            if ((m = TodoLine.Match(line)).Success)
            {
                lists.Clear();
                var p = new Paragraph();
                AddInlines(p, m.Groups[2].Value);
                MakeTodo(p, m.Groups[1].Value is "x" or "X");
                blocks.Add(p);
                continue;
            }
            if ((m = ListLine.Match(line)).Success)
            {
                int depth = Math.Min(m.Groups[1].Value.Length / 2, 6);
                var style = char.IsDigit(m.Groups[2].Value[0]) ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                while (lists.Count > depth + 1) lists.RemoveAt(lists.Count - 1);
                if (lists.Count == depth + 1 && lists[depth].MarkerStyle != style) lists.RemoveAt(depth);
                if (lists.Count <= depth)
                {
                    var list = new List { MarkerStyle = style, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(22, 0, 0, 0) };
                    if (depth == 0 || lists.Count == 0) blocks.Add(list);
                    else (lists[^1].ListItems.LastListItem ?? Add(lists[^1])).Blocks.Add(list);
                    while (lists.Count < depth) lists.Add(list);
                    lists.Add(list);
                }
                var item = new ListItem();
                var ip = new Paragraph { Margin = new Thickness(0) };
                AddInlines(ip, m.Groups[3].Value);
                item.Blocks.Add(ip);
                lists[depth].ListItems.Add(item);
                continue;
            }
            lists.Clear();
            var para = new Paragraph();
            string? kind = null;
            var body = line;
            if (line.StartsWith("### ")) { kind = "h3"; body = line[4..]; }
            else if (line.StartsWith("## ")) { kind = "h2"; body = line[3..]; }
            else if (line.StartsWith("# ")) { kind = "h1"; body = line[2..]; }
            else if (line.StartsWith("> ")) { kind = "quote"; body = line[2..]; }
            else if (line.Trim() == "---") { kind = "hr"; body = ""; }
            AddInlines(para, body);
            StyleBlock(para, kind);
            blocks.Add(para);
        }
        // a note ending in a list keeps a plain line after it to type into
        if (blocks.LastOrDefault() is List) { var tail = new Paragraph(); StyleBlock(tail, null); blocks.Add(tail); }
        return blocks;
    }

    static ListItem Add(List list) { var li = new ListItem(); list.ListItems.Add(li); return li; }

    static readonly Regex InlineFormat = new(@"\*\*(.+?)\*\*|~~(.+?)~~|\*(.+?)\*");

    static void AddInlines(Paragraph p, string text)
    {
        int at = 0;
        foreach (Match m in InlineFormat.Matches(text))
        {
            if (m.Index > at) p.Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success) p.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.Bold });
            else if (m.Groups[2].Success) p.Inlines.Add(new Run(m.Groups[2].Value) { TextDecorations = TextDecorations.Strikethrough });
            else p.Inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic });
            at = m.Index + m.Length;
        }
        if (at < text.Length) p.Inlines.Add(new Run(text[at..]));
    }

    /// <summary>Round trip for the self-test (no UI shown).</summary>
    public static string RoundTrip(string md)
    {
        var e = new NotesEditor();
        e.Load(md, force: true);
        return e.Serialize();
    }
}
