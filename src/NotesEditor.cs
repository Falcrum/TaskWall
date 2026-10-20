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
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// The day's notebook (next to the day view's timeline), a little like a Notion page, kept as Markdown (it syncs as
/// plain text and stays readable): headings 1–6, bold / italic / strike / inline code, links, bullet and numbered lists,
/// check boxes, quotes, code blocks and dividers. The Markdown marks format as they are typed: "# ", "- ", "1. ", "[] ",
/// "> " at the start of a line, "---" or "```" + Enter, **bold**, *italic*, ~~strike~~, `code`, [text](https://…).
/// </summary>
public sealed class NotesEditor : DockPanel
{
    readonly RichTextBox _box = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), AcceptsTab = true, AcceptsReturn = true, IsDocumentEnabled = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 4, 4, 4), UndoLimit = 200,
    };
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _loading;
    string _last = "";
    double Base => App.Settings.FontSize;
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>The text changed (Markdown), a moment after the last key.</summary>
    public event Action<string>? Changed;
    /// <summary>The export button (the day view knows the other days' notes).</summary>
    public event Action<FrameworkElement>? ExportClicked;
    public bool Editing => _box.IsKeyboardFocusWithin;
    public string Markdown => Serialize();

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
        Button Tool(string text, string tip, Action a, FontWeight? weight = null, FontStyle? style = null, TextDecorationCollection? deco = null, FontFamily? font = null)
        {
            var tb = new TextBlock { Text = text, FontFamily = font ?? (FontFamily)Application.Current.Resources["UiFont"], FontSize = 12, FontWeight = weight ?? FontWeights.SemiBold, FontStyle = style ?? FontStyles.Normal, TextDecorations = deco };
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = tb, ToolTip = tip, Padding = new Thickness(7, 2, 7, 3), Focusable = false };
            b.Click += (_, _) => { a(); _box.Focus(); };
            bar.Children.Add(b);
            return b;
        }
        void Sep() => bar.Children.Add(new Border { Width = 1, Height = 14, Margin = new Thickness(4, 0, 4, 0), Background = Res("Line") });
        Tool("H1", L.T("Nagłówek 1  (# spacja)"), () => SetBlock("h1"));
        Tool("H2", L.T("Nagłówek 2  (## spacja)"), () => SetBlock("h2"));
        Tool("H3", L.T("Nagłówek 3  (### spacja)"), () => SetBlock("h3"));
        Tool("¶", L.T("Zwykły tekst"), () => SetBlock(null));
        Sep();
        Tool("B", L.T("Pogrubienie  (Ctrl+B, **tekst**)"), () => EditingCommands.ToggleBold.Execute(null, _box), FontWeights.Bold);
        Tool("I", L.T("Kursywa  (Ctrl+I, *tekst*)"), () => EditingCommands.ToggleItalic.Execute(null, _box), FontWeights.Normal, FontStyles.Italic);
        Tool("S", L.T("Przekreślenie  (Ctrl+Shift+S, ~~tekst~~)"), ToggleStrike, FontWeights.Normal, null, TextDecorations.Strikethrough);
        Tool("</>", L.T("Kod  (Ctrl+E, `tekst`; ``` + Enter: blok kodu)"), ToggleCode, FontWeights.Normal, null, null, Mono);
        Sep();
        Tool("•", L.T("Lista  (- spacja)"), () => EditingCommands.ToggleBullets.Execute(null, _box));
        Tool("1.", L.T("Lista numerowana  (1. spacja)"), () => EditingCommands.ToggleNumbering.Execute(null, _box));
        Tool("☐", L.T("Pole wyboru  ([] spacja)"), ToggleTodo);
        Tool("❝", L.T("Cytat  (> spacja)"), () => SetBlock("quote"));
        Tool("—", L.T("Linia oddzielająca  (---)"), InsertDivider);
        Sep();
        Tool("↶", L.T("Cofnij  (Ctrl+Z)"), () => { if (_box.CanUndo) _box.Undo(); }, FontWeights.Normal);
        Tool("↷", L.T("Ponów  (Ctrl+Y)"), () => { if (_box.CanRedo) _box.Redo(); }, FontWeights.Normal);
        var export = Tool("⤓", L.T("Eksportuj notatki do .md / .txt"), () => { }, FontWeights.Normal);
        export.Click += (_, _) => ExportClicked?.Invoke(export);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var area = new Grid();
        area.Children.Add(_box);
        var hint = new TextBlock { Text = L.T("Pisz tutaj…  # nagłówek, - lista, [] pole wyboru, **pogrubienie**"), Foreground = Res("FgFaint"), FontSize = Base - 0.5, Margin = new Thickness(2, 6, 0, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
        area.Children.Add(hint);
        void Hint() => hint.Visibility = _box.Document.Blocks.Count <= 1 && _box.Document.Blocks.FirstBlock is Paragraph { Tag: null } fp && new TextRange(fp.ContentStart, fp.ContentEnd).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        _box.TextChanged += (_, _) => Hint();
        Children.Add(area);

        _box.TextChanged += (_, _) => { if (!_loading) { _save.Stop(); _save.Start(); } };
        _save.Tick += (_, _) => { _save.Stop(); Flush(); };
        _box.PreviewKeyDown += OnKey;
        _box.AddHandler(TextInputEvent, new TextCompositionEventHandler((_, e) => { if (e.Text is "*" or "_" or "~" or "`" or ")") InlineAuto(); }), true);
        _box.LostKeyboardFocus += (_, _) => Flush();
    }

    /// <summary>Shows a note (Markdown). Ignored while the user types into it (their version wins and is saved).</summary>
    public void Load(string markdown, bool force = false)
    {
        if (!force && (Editing || markdown == _last)) return;
        _loading = true;
        _box.IsUndoEnabled = false; // a fresh note: Ctrl+Z never undoes it into the previous day's text
        _box.Document.Blocks.Clear();
        foreach (var b in Parse(markdown)) _box.Document.Blocks.Add(b);
        if (_box.Document.Blocks.Count == 0) _box.Document.Blocks.Add(new Paragraph());
        _box.IsUndoEnabled = true;
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
        p.ClearValue(Block.BackgroundProperty);
        p.ClearValue(TextElement.FontFamilyProperty);
        p.ClearValue(TextElement.FontStyleProperty);
        p.Margin = new Thickness(0, 2, 0, 2);
        switch (kind)
        {
            case "h1": p.FontSize = Base * 1.6; p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 10, 0, 4); break;
            case "h2": p.FontSize = Base * 1.32; p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 8, 0, 3); break;
            case "h3": p.FontSize = Base * 1.12; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 6, 0, 2); break;
            case "h4": case "h5": case "h6":
                p.FontSize = Base * (kind == "h4" ? 1.02 : 0.95); p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 5, 0, 1);
                if (kind == "h6") p.Foreground = Res("FgDim");
                break;
            case "quote":
                p.BorderBrush = Res("AccentBrush"); p.BorderThickness = new Thickness(2.5, 0, 0, 0); p.Padding = new Thickness(10, 1, 0, 1);
                p.Foreground = Res("FgDim"); break;
            case "hr": p.BorderBrush = Res("Line"); p.BorderThickness = new Thickness(0, 0, 0, 1); p.Margin = new Thickness(0, 6, 0, 8); p.FontSize = 2; break;
            case "todo": p.Margin = new Thickness(0, 3, 0, 3); break;
            case "code":
                p.FontFamily = Mono; p.FontSize = Base - 1; p.Background = new SolidColorBrush(Th.Ink(0x12)); p.Padding = new Thickness(8, 1, 8, 1);
                p.Margin = new Thickness(0); break;
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

    void ToggleCode()
    {
        var sel = _box.Selection;
        bool on = sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && f.Source == Mono.Source;
        if (on)
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, _box.Document.FontFamily);
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, null);
        }
        else
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, Mono);
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Th.Ink(0x18)));
        }
    }

    // ---------- inline Markdown as it is typed: **bold**, *italic*, ~~strike~~, `code`, [text](url) ----------

    static readonly (Regex Re, string Kind)[] InlineRules =
    {
        (new(@"\[([^\]]+)\]\(([^)\s]+)\)$"), "link"),
        (new(@"`([^`]+)`$"), "code"),
        (new(@"\*\*([^*\s](?:[^*]*[^*\s])?)\*\*$"), "bold"),
        (new(@"__([^_\s](?:[^_]*[^_\s])?)__$"), "bold"),
        (new(@"~~([^~\s](?:[^~]*[^~\s])?)~~$"), "strike"),
        (new(@"(?<![*\w])\*([^*\s](?:[^*]*[^*\s])?)\*$"), "italic"),
        (new(@"(?<![_\w])_([^_\s](?:[^_]*[^_\s])?)_$"), "italic"),
    };

    void InlineAuto()
    {
        var caret = _box.CaretPosition;
        if (caret.Paragraph is { Tag: "code" }) return;
        var before = caret.GetTextInRun(LogicalDirection.Backward);
        foreach (var (re, kind) in InlineRules)
        {
            var m = re.Match(before);
            if (!m.Success) continue;
            if (caret.GetPositionAtOffset(-m.Length) is not { } start) return;
            var range = new TextRange(start, caret);
            range.Text = m.Groups[1].Value;
            switch (kind)
            {
                case "bold": range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold); break;
                case "italic": range.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic); break;
                case "strike": range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough); break;
                case "code":
                    range.ApplyPropertyValue(TextElement.FontFamilyProperty, Mono);
                    range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Th.Ink(0x18)));
                    break;
                case "link":
                    var link = MakeLink(range.Start, range.End, m.Groups[2].Value);
                    _box.CaretPosition = link.ElementEnd;
                    return;
            }
            // what is typed next is plain again
            _box.Selection.Select(range.End, range.End);
            _box.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            _box.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            _box.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            _box.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, _box.Document.FontFamily);
            _box.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, null);
            return;
        }
    }

    static Hyperlink MakeLink(TextPointer from, TextPointer to, string url) => Link(new Hyperlink(from, to), url);

    static Hyperlink Link(Hyperlink link, string url)
    {
        link.Foreground = Res("AccentBrush");
        link.Cursor = Cursors.Hand;
        link.ToolTip = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        link.Tag = url;
        link.RequestNavigate += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        };
        return link;
    }

    // ---------- check box lines ----------

    void ToggleTodo()
    {
        if (Current is not { } p) return;
        if (p.Tag as string == "todo") { RemoveCheck(p); StyleBlock(p, null); }
        else MakeTodo(p, false);
    }

    /// <summary>A small square check box at the start of the line (clickable).</summary>
    sealed class TodoBox : Border
    {
        public bool IsChecked { get; private set; }
        public event Action<bool>? Toggled;
        readonly Path _tick;

        public TodoBox(bool on, double size)
        {
            Width = Height = size;
            CornerRadius = new CornerRadius(3);
            BorderThickness = new Thickness(1.3);
            Cursor = Cursors.Hand;
            Margin = new Thickness(0, 0, 7, 0);
            _tick = new Path
            {
                Data = Geometry.Parse("M1.5,5 L4,7.5 L8.5,2"), Stroke = Brushes.White, StrokeThickness = 1.6, Stretch = Stretch.Uniform,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(1.6),
            };
            Child = _tick;
            Set(on);
            MouseEnter += (_, _) => { if (!IsChecked) BorderBrush = Res("AccentBrush"); };
            MouseLeave += (_, _) => { if (!IsChecked) BorderBrush = Res("FgDim"); };
            MouseLeftButtonDown += (_, e) => { e.Handled = true; Set(!IsChecked); Toggled?.Invoke(IsChecked); };
        }

        void Set(bool on)
        {
            IsChecked = on;
            Background = on ? Res("AccentBrush") : Brushes.Transparent;
            BorderBrush = on ? Res("AccentBrush") : Res("FgDim");
            _tick.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
    }

    void MakeTodo(Paragraph p, bool done)
    {
        StyleBlock(p, "todo");
        var cb = new TodoBox(done, Math.Round(Base * 0.95));
        cb.Toggled += on => { DimTodo(p, on); Flush(); };
        var holder = new InlineUIContainer(cb) { BaselineAlignment = BaselineAlignment.Center };
        if (p.Inlines.FirstInline is { } first) p.Inlines.InsertBefore(first, holder); else p.Inlines.Add(holder);
        DimTodo(p, done);
    }

    static void DimTodo(Paragraph p, bool done)
    {
        foreach (var run in Runs(p.Inlines))
        {
            run.TextDecorations = done ? TextDecorations.Strikethrough : null;
            if (done) run.Foreground = Res("FgFaint"); else run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    static void RemoveCheck(Paragraph p)
    {
        if (p.Inlines.FirstInline is InlineUIContainer { Child: TodoBox }) p.Inlines.Remove(p.Inlines.FirstInline);
        DimTodo(p, false);
    }

    static bool IsTodoChecked(Paragraph p) => p.Inlines.FirstInline is InlineUIContainer { Child: TodoBox { IsChecked: true } };

    void OnKey(object sender, KeyEventArgs e)
    {
        // shortcuts of the notebook (the board's own keys never reach here while typing)
        if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; ToggleStrike(); return; }
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ToggleCode(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3)
        {
            e.Handled = true;
            SetBlock(e.Key == Key.D1 ? "h1" : e.Key == Key.D2 ? "h2" : "h3");
            return;
        }
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Current is { } p && p.Parent is FlowDocument && p.Tag as string != "code")
        {
            // "# " … "###### ", "- " "* " "+ ", "1. ", "[] " "[x] ", "> " at the start of a line
            var before = new TextRange(p.ContentStart, _box.CaretPosition).Text;
            string? kind = before switch
            {
                "#" => "h1", "##" => "h2", "###" => "h3", "####" => "h4", "#####" => "h5", "######" => "h6", ">" => "quote",
                "-" or "*" or "+" => "bullet", "[]" or "[ ]" => "todo", "[x]" or "[X]" => "done",
                _ => Regex.IsMatch(before, @"^\d+\.$") ? "number" : null,
            };
            if (kind == null) return;
            e.Handled = true;
            new TextRange(p.ContentStart, _box.CaretPosition).Text = "";
            if (kind == "bullet") EditingCommands.ToggleBullets.Execute(null, _box);
            else if (kind == "number") EditingCommands.ToggleNumbering.Execute(null, _box);
            else if (kind is "todo" or "done") MakeTodo(p, kind == "done");
            else StyleBlock(p, kind);
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Current is { } cur)
        {
            var kind = cur.Tag as string;
            var text = new TextRange(cur.ContentStart, cur.ContentEnd).Text.Trim();
            if (kind == "todo" && text.Length == 0) { e.Handled = true; RemoveCheck(cur); StyleBlock(cur, null); return; } // an empty line ends the list
            if (kind == "code" && text.Length == 0) { e.Handled = true; StyleBlock(cur, null); return; } // … and the code block
            if (kind != "code" && cur.Parent is FlowDocument doc && (Regex.IsMatch(text, @"^(-{3,}|\*{3,}|_{3,})$") || text.StartsWith("```")))
            {
                e.Handled = true;
                bool code = text.StartsWith("```");
                new TextRange(cur.ContentStart, cur.ContentEnd).Text = "";
                if (code) { StyleBlock(cur, "code"); return; }
                StyleBlock(cur, "hr");
                var next = new Paragraph();
                StyleBlock(next, null);
                doc.Blocks.InsertAfter(cur, next);
                _box.CaretPosition = next.ContentStart;
                return;
            }
            // after the split: a heading is followed by plain text, a check box line by another check box line, code by code
            Dispatcher.BeginInvoke(() =>
            {
                if (Current is not { } np || ReferenceEquals(np, cur)) return;
                if (kind is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "quote" or "hr") StyleBlock(np, null);
                else if (kind == "code") StyleBlock(np, "code");
                else if (kind == "todo") { RemoveCheck(np); while (np.Inlines.FirstInline is InlineUIContainer u) np.Inlines.Remove(u); DimTodo(np, false); MakeTodo(np, false); }
            }, DispatcherPriority.Input);
        }
    }

    // ---------- Markdown out ----------

    string Serialize()
    {
        var sb = new StringBuilder();
        bool code = false;
        foreach (var b in _box.Document.Blocks)
        {
            bool isCode = b is Paragraph { Tag: "code" };
            if (isCode != code) { sb.Append("```\n"); code = isCode; }
            if (isCode) sb.Append(new TextRange(b.ContentStart, b.ContentEnd).Text).Append('\n');
            else Write(sb, b, 0);
        }
        if (code) sb.Append("```\n");
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
                    "h1" => "# " + text, "h2" => "## " + text, "h3" => "### " + text, "h4" => "#### " + text, "h5" => "##### " + text, "h6" => "###### " + text,
                    "quote" => "> " + text, "hr" => "---",
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
        foreach (var i in inlines)
        {
            switch (i)
            {
                case Hyperlink h: sb.Append('[').Append(Inlines(h.Inlines)).Append("](").Append(h.Tag as string ?? h.NavigateUri?.OriginalString ?? "").Append(')'); break;
                case Span s: sb.Append(Inlines(s.Inlines)); break;
                case Run r: sb.Append(Format(r)); break;
                case LineBreak: sb.Append(' '); break;
            }
        }
        return sb.ToString().Replace("****", "").Replace("~~~~", "");
    }

    static string Format(Run run)
    {
        var t = run.Text;
        if (t.Length == 0) return "";
        if (run.FontFamily?.Source == Mono.Source && FindParagraph(run) is not { Tag: "code" }) return "`" + t + "`";
        var para = FindParagraph(run);
        bool bold = run.FontWeight >= FontWeights.SemiBold && para is not { Tag: "h1" or "h2" or "h3" or "h4" or "h5" or "h6" };
        bool italic = run.FontStyle == FontStyles.Italic;
        bool strike = run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true && para is not { Tag: "todo" };
        // markers hug the words: "**word** " – spaces stay outside
        var lead = t[..(t.Length - t.TrimStart().Length)];
        var trail = t[t.TrimEnd().Length..];
        var core = t.Trim();
        if (core.Length == 0) return t;
        if (strike) core = "~~" + core + "~~";
        if (italic) core = "*" + core + "*";
        if (bold) core = "**" + core + "**";
        return lead + core + trail;
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
        }
    }

    // ---------- Markdown in ----------

    static readonly Regex ListLine = new(@"^(\s*)([-*+]|\d+\.)\s+(.*)$");
    static readonly Regex TodoLine = new(@"^\s*[-*+]\s+\[( |x|X)\]\s?(.*)$");
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");

    IEnumerable<Block> Parse(string md)
    {
        var blocks = new List<Block>();
        var lists = new List<List>(); // the open list per depth
        bool code = false;
        foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { code = !code; lists.Clear(); continue; }
            if (code)
            {
                var cp = new Paragraph(new Run(raw));
                StyleBlock(cp, "code");
                blocks.Add(cp);
                continue;
            }
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
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                lists.Clear();
                var hr = new Paragraph();
                StyleBlock(hr, "hr");
                blocks.Add(hr);
                continue;
            }
            if ((m = ListLine.Match(line)).Success)
            {
                int depth = Math.Min(m.Groups[1].Value.Replace("\t", "  ").Length / 2, 6);
                var style = char.IsDigit(m.Groups[2].Value[0]) ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                while (lists.Count > depth + 1) lists.RemoveAt(lists.Count - 1);
                if (lists.Count == depth + 1 && lists[depth].MarkerStyle != style) lists.RemoveAt(depth);
                if (lists.Count <= depth)
                {
                    var list = new List { MarkerStyle = style, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(22, 0, 0, 0) };
                    if (style == TextMarkerStyle.Decimal && int.TryParse(m.Groups[2].Value.TrimEnd('.'), out var startAt) && startAt > 1) list.StartIndex = startAt;
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
            if ((m = Heading.Match(line)).Success) { kind = "h" + m.Groups[1].Value.Length; body = m.Groups[2].Value; }
            else if (line.StartsWith("> ")) { kind = "quote"; body = line[2..]; }
            else if (line == ">") { kind = "quote"; body = ""; }
            AddInlines(para, body);
            StyleBlock(para, kind);
            blocks.Add(para);
        }
        // a note ending in a list / code keeps a plain line after it to type into
        if (blocks.LastOrDefault() is List or Paragraph { Tag: "code" }) { var tail = new Paragraph(); StyleBlock(tail, null); blocks.Add(tail); }
        return blocks;
    }

    static ListItem Add(List list) { var li = new ListItem(); list.ListItems.Add(li); return li; }

    static readonly Regex InlineFormat = new(@"\[([^\]]+)\]\(([^)\s]+)\)|`([^`]+)`|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|(?<![*\w])\*([^*]+?)\*(?!\*)|(?<![_\w])_([^_]+?)_(?!\w)");

    static void AddInlines(Paragraph p, string text)
    {
        int at = 0;
        foreach (Match m in InlineFormat.Matches(text))
        {
            if (m.Index > at) p.Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success)
            {
                p.Inlines.Add(Link(new Hyperlink(new Run(m.Groups[1].Value)), m.Groups[2].Value));
            }
            else if (m.Groups[3].Success) p.Inlines.Add(new Run(m.Groups[3].Value) { FontFamily = Mono, Background = new SolidColorBrush(Th.Ink(0x18)) });
            else if (m.Groups[4].Success || m.Groups[5].Success) p.Inlines.Add(new Run(m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value) { FontWeight = FontWeights.Bold });
            else if (m.Groups[6].Success) p.Inlines.Add(new Run(m.Groups[6].Value) { TextDecorations = TextDecorations.Strikethrough });
            else p.Inlines.Add(new Run(m.Groups[7].Success ? m.Groups[7].Value : m.Groups[8].Value) { FontStyle = FontStyles.Italic });
            at = m.Index + m.Length;
        }
        if (at < text.Length) p.Inlines.Add(new Run(text[at..]));
    }

    /// <summary>Markdown → plain text (for the .txt export): no marks, lists and boxes as characters.</summary>
    public static string ToPlain(string md)
    {
        var sb = new StringBuilder();
        bool code = false;
        foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { code = !code; continue; }
            if (code) { sb.Append("    ").Append(raw).Append('\n'); continue; }
            Match m;
            if ((m = Heading.Match(line)).Success)
            {
                var h = Strip(m.Groups[2].Value);
                sb.Append(m.Groups[1].Value.Length == 1 ? h.ToUpperInvariant() : h).Append('\n');
                if (m.Groups[1].Value.Length <= 2) sb.Append(new string(m.Groups[1].Value.Length == 1 ? '=' : '-', Math.Max(3, h.Length))).Append('\n');
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$")) { sb.Append(new string('─', 24)).Append('\n'); continue; }
            if ((m = TodoLine.Match(line)).Success) { sb.Append(new string(' ', line.Length - line.TrimStart().Length)).Append(m.Groups[1].Value is "x" or "X" ? "☑ " : "☐ ").Append(Strip(m.Groups[2].Value)).Append('\n'); continue; }
            if ((m = ListLine.Match(line)).Success) { sb.Append(m.Groups[1].Value).Append(char.IsDigit(m.Groups[2].Value[0]) ? m.Groups[2].Value + " " : "• ").Append(Strip(m.Groups[3].Value)).Append('\n'); continue; }
            if (line.StartsWith("> ")) { sb.Append("│ ").Append(Strip(line[2..])).Append('\n'); continue; }
            sb.Append(Strip(line)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');

        static string Strip(string s) => InlineFormat.Replace(s, m =>
            m.Groups[1].Success ? $"{m.Groups[1].Value} ({m.Groups[2].Value})"
            : Enumerable.Range(3, 6).Select(i => m.Groups[i]).First(g => g.Success).Value);
    }

    /// <summary>Round trip for the self-test (no UI shown).</summary>
    public static string RoundTrip(string md)
    {
        var e = new NotesEditor();
        e.Load(md, force: true);
        return e.Serialize();
    }
}
