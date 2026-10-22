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
using System.Windows.Navigation;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// The day's notebook (next to the day view's timeline), kept as Markdown (it syncs as plain text and stays readable).
/// Two ways to write, like Typora: formatted (the Markdown marks turn into formatting as they are typed – "# ", "- ",
/// "1. ", "[] ", "> ", "---" / "```" + Enter, **bold**, *italic*, ~~strike~~, `code`, [text](url); pasted Markdown is
/// formatted too) and the plain Markdown source. Headings 1–6, bold / italic / strike / code, links, bullet and numbered
/// lists, check boxes, quotes, code blocks and dividers.
/// Check boxes are characters (☐ / ☑) at the start of the line – click them; Backspace and Ctrl+Z treat them as text
/// (no controls inside the text: those broke deleting and undo).
/// </summary>
public sealed class NotesEditor : DockPanel
{
    readonly RichTextBox _box = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), AcceptsTab = true, AcceptsReturn = true, IsDocumentEnabled = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 4, 4, 4), UndoLimit = 300,
    };
    readonly TextBox _src = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), AcceptsTab = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 4, 4, 4), UndoLimit = 300, Visibility = Visibility.Collapsed,
    };
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly List<(Button Button, bool Source)> _modeButtons = new();
    readonly TextBlock _hint = new();
    bool _loading;
    bool _source;
    string _last = "";
    double Base => App.Settings.FontSize;
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    static readonly FontFamily Symbols = new("Segoe UI Symbol");
    const string Unchecked = "☐", Checked = "☑";

    /// <summary>The text changed (Markdown), a moment after the last key.</summary>
    public event Action<string>? Changed;
    /// <summary>The export button (the day view knows the other days' notes).</summary>
    public event Action<FrameworkElement>? ExportClicked;
    public bool Editing => _box.IsKeyboardFocusWithin || _src.IsKeyboardFocusWithin;
    public string Markdown => _source ? _src.Text.Replace("\r", "") : Serialize();

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public NotesEditor()
    {
        _box.Foreground = Res("Fg");
        _box.CaretBrush = Res("Fg");
        _box.SelectionBrush = Res("AccentBrush");
        _box.Document.PagePadding = new Thickness(0);
        _box.Document.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _box.Document.FontSize = Base;
        _src.Foreground = Res("Fg");
        _src.CaretBrush = Res("Fg");
        _src.SelectionBrush = Res("AccentBrush");
        _src.FontFamily = Mono;
        _src.FontSize = Base - 0.5;

        Children.Add(BuildToolbar());
        var area = new Grid();
        area.Children.Add(_box);
        area.Children.Add(_src);
        _hint.Text = L.T("Pisz tutaj…  # nagłówek, - lista, [] pole wyboru, **pogrubienie**");
        _hint.Foreground = Res("FgFaint");
        _hint.FontSize = Base - 0.5;
        _hint.Margin = new Thickness(2, 6, 0, 0);
        _hint.IsHitTestVisible = false;
        _hint.TextWrapping = TextWrapping.Wrap;
        area.Children.Add(_hint);
        Children.Add(area);

        _box.TextChanged += (_, _) => { UpdateHint(); if (!_loading) { _save.Stop(); _save.Start(); } };
        _src.TextChanged += (_, _) => { UpdateHint(); if (!_loading) { _save.Stop(); _save.Start(); } };
        _save.Tick += (_, _) => { _save.Stop(); Flush(); };
        _box.PreviewKeyDown += OnKey;
        _box.AddHandler(TextInputEvent, new TextCompositionEventHandler((_, e) => { if (e.Text is "*" or "_" or "~" or "`" or ")") InlineAuto(); }), true);
        _box.PreviewMouseLeftButtonDown += OnBoxMouseDown;
        _box.SelectionChanged += (_, _) => KeepCaretAfterCheck();
        _box.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }));
        DataObject.AddPastingHandler(_box, OnPaste);
        _box.LostKeyboardFocus += (_, _) => Flush();
        _src.LostKeyboardFocus += (_, _) => Flush();
        SetMode(App.Settings.NotesSource, save: false);
    }

    // ---------- toolbar: every feature has a button; in the Markdown mode the buttons write the marks ----------

    FrameworkElement BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        SetDock(bar, Dock.Top);
        Button Tool(string text, string tip, Action rich, Action? source, FontWeight? weight = null, FontStyle? style = null, TextDecorationCollection? deco = null, FontFamily? font = null, double size = 12)
        {
            var tb = new TextBlock { Text = text, FontFamily = font ?? (FontFamily)Application.Current.Resources["UiFont"], FontSize = size, FontWeight = weight ?? FontWeights.SemiBold, FontStyle = style ?? FontStyles.Normal, TextDecorations = deco };
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = tb, ToolTip = tip, Padding = new Thickness(6, 2, 6, 3), Focusable = false, MinWidth = 24 };
            ToolTipService.SetInitialShowDelay(b, 350);
            b.Click += (_, _) =>
            {
                if (_source) { (source ?? (() => { }))(); _src.Focus(); }
                else { rich(); _box.Focus(); }
            };
            bar.Children.Add(b);
            return b;
        }
        void Sep() => bar.Children.Add(new Border { Width = 1, Height = 14, Margin = new Thickness(4, 0, 4, 0), Background = Res("Line"), VerticalAlignment = VerticalAlignment.Center });

        // the two ways of writing
        foreach (var (label, tip, src) in new[] { ("Aa", L.T("Tekst sformatowany (znaki Markdown od razu formatują tekst)"), false), ("MD", L.T("Źródło Markdown (czysty tekst ze znakami)"), true) })
        {
            var b = Tool(label, tip, () => { }, null, FontWeights.Bold, size: 11);
            b.Click += (_, _) => SetMode(src, save: true);
            _modeButtons.Add((b, src));
        }
        Sep();
        Tool("H1", L.T("Nagłówek 1  (# spacja, Ctrl+1)"), () => SetBlock("h1"), () => SrcPrefix("# "));
        Tool("H2", L.T("Nagłówek 2  (## spacja, Ctrl+2)"), () => SetBlock("h2"), () => SrcPrefix("## "));
        Tool("H3", L.T("Nagłówek 3  (### spacja, Ctrl+3)"), () => SetBlock("h3"), () => SrcPrefix("### "));
        Tool("H4", L.T("Nagłówek 4  (#### spacja)"), () => SetBlock("h4"), () => SrcPrefix("#### "));
        Tool("¶", L.T("Zwykły tekst  (Ctrl+0)"), () => SetBlock(null), () => SrcPrefix(""));
        Sep();
        Tool("B", L.T("Pogrubienie  (Ctrl+B, **tekst**)"), () => EditingCommands.ToggleBold.Execute(null, _box), () => SrcWrap("**"), FontWeights.Bold);
        Tool("I", L.T("Kursywa  (Ctrl+I, *tekst*)"), () => EditingCommands.ToggleItalic.Execute(null, _box), () => SrcWrap("*"), FontWeights.Normal, FontStyles.Italic);
        Tool("S", L.T("Przekreślenie  (Ctrl+Shift+S, ~~tekst~~)"), ToggleStrike, () => SrcWrap("~~"), FontWeights.Normal, null, TextDecorations.Strikethrough);
        Tool("</>", L.T("Kod w tekście  (Ctrl+E, `tekst`)"), ToggleCode, () => SrcWrap("`"), FontWeights.Normal, null, null, Mono, 11);
        Tool("🔗", L.T("Link  (Ctrl+K, [tekst](https://…))"), InsertLink, SrcLink, FontWeights.Normal, font: Symbols, size: 11);
        Tool("Tx", L.T("Usuń formatowanie zaznaczenia"), () => _box.Selection.ClearAllProperties(), SrcStrip, FontWeights.Normal, null, TextDecorations.Strikethrough, size: 11);
        Sep();
        Tool("•", L.T("Lista  (- spacja)"), () => EditingCommands.ToggleBullets.Execute(null, _box), () => SrcPrefix("- "));
        Tool("1.", L.T("Lista numerowana  (1. spacja)"), () => EditingCommands.ToggleNumbering.Execute(null, _box), () => SrcPrefix("1. "));
        Tool(Unchecked, L.T("Pole wyboru  ([] spacja; klik w pole zaznacza)"), ToggleTodo, () => SrcPrefix("- [ ] "), FontWeights.Normal, font: Symbols, size: 13);
        Tool("⇥", L.T("Wcięcie listy  (Tab)"), () => EditingCommands.IncreaseIndentation.Execute(null, _box), () => SrcIndent(true), FontWeights.Normal, font: Symbols);
        Tool("⇤", L.T("Mniejsze wcięcie  (Shift+Tab)"), () => EditingCommands.DecreaseIndentation.Execute(null, _box), () => SrcIndent(false), FontWeights.Normal, font: Symbols);
        Sep();
        Tool("❝", L.T("Cytat  (> spacja)"), () => SetBlock("quote"), () => SrcPrefix("> "), FontWeights.Normal, font: Symbols);
        Tool("{ }", L.T("Blok kodu  (``` + Enter)"), () => SetBlock("code"), () => SrcFence(), FontWeights.Normal, font: Mono, size: 11);
        Tool("—", L.T("Linia oddzielająca  (--- + Enter)"), InsertDivider, () => SrcInsertLine("---"), FontWeights.Normal);
        Sep();
        Tool("↶", L.T("Cofnij  (Ctrl+Z)"), () => { if (_box.CanUndo) _box.Undo(); }, () => { if (_src.CanUndo) _src.Undo(); }, FontWeights.Normal, font: Symbols);
        Tool("↷", L.T("Ponów  (Ctrl+Y)"), () => { if (_box.CanRedo) _box.Redo(); }, () => { if (_src.CanRedo) _src.Redo(); }, FontWeights.Normal, font: Symbols);
        Sep();
        var export = Tool("⤓", L.T("Eksportuj notatki do .md / .txt"), () => { }, null, FontWeights.Normal, font: Symbols);
        export.Click += (_, _) => ExportClicked?.Invoke(export);
        Tool("🗑", L.T("Wyczyść notatkę tego dnia (Ctrl+Z cofa)"), ClearAll, ClearAll, FontWeights.Normal, font: Symbols, size: 11);
        return bar;
    }

    /// <summary>The source box gets new text without an undo step (Ctrl+Z never brings back another day's note).</summary>
    void SetSource(string text)
    {
        _src.IsUndoEnabled = false;
        _src.Text = text;
        _src.IsUndoEnabled = true;
    }

    void SetMode(bool source, bool save)
    {
        if (save) Flush(); // what was just typed is saved before the other way of writing takes over
        if (source != _source)
        {
            if (source)
            {
                _loading = true;
                SetSource(Serialize());
                _loading = false;
            }
            else Load(_src.Text.Replace("\r", ""), force: true);
            _source = source;
        }
        _box.Visibility = _source ? Visibility.Collapsed : Visibility.Visible;
        _src.Visibility = _source ? Visibility.Visible : Visibility.Collapsed;
        if (save) Anim.Enter(_source ? _src : _box, 0, 4, 200); // a soft change of the way of writing
        foreach (var (b, src) in _modeButtons)
        {
            b.Background = src == _source ? (Brush)Application.Current.Resources["AccentSoft"] : Brushes.Transparent;
            if (b.Content is TextBlock t) t.Foreground = src == _source ? Res("Fg") : Res("FgDim");
        }
        UpdateHint();
        if (save && App.Settings.NotesSource != source) { App.Settings.NotesSource = source; SettingsStore.Save(App.Settings); }
    }

    void UpdateHint() => _hint.Visibility = (_source ? _src.Text.Length == 0
        : _box.Document.Blocks.Count <= 1 && _box.Document.Blocks.FirstBlock is Paragraph { Tag: null } fp && new TextRange(fp.ContentStart, fp.ContentEnd).IsEmpty)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows a note (Markdown). Ignored while the user types into it (their version wins and is saved).</summary>
    public void Load(string markdown, bool force = false)
    {
        if (!force && (Editing || markdown == _last)) return;
        _loading = true;
        try
        {
            if (_source) SetSource(markdown);
            _box.IsUndoEnabled = false; // a fresh note: Ctrl+Z never undoes it into the previous day's text
            _box.Document.Blocks.Clear();
            foreach (var b in Parse(markdown)) _box.Document.Blocks.Add(b);
            if (_box.Document.Blocks.Count == 0) _box.Document.Blocks.Add(new Paragraph());
            _box.IsUndoEnabled = true;
            _last = markdown;
        }
        finally { _loading = false; }
        UpdateHint();
    }

    public void Flush()
    {
        if (_loading) return;
        var md = Markdown;
        if (md == _last) return;
        _last = md;
        Changed?.Invoke(md);
    }

    void ClearAll()
    {
        if (Markdown.Trim().Length == 0) return;
        if (MessageBox.Show(L.T("Wyczyścić całą notatkę z tego dnia?\nCtrl+Z cofa."), "TaskWall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_source) { _src.SelectAll(); _src.SelectedText = ""; _src.Focus(); }
        else
        {
            _box.BeginChange();
            new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd).Text = "";
            foreach (var p in _box.Document.Blocks.OfType<Paragraph>()) StyleBlock(p, null);
            _box.EndChange();
            _box.Focus();
        }
        Flush();
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
        _box.BeginChange();
        if (IsTodo(p)) RemoveCheck(p);
        StyleBlock(p, p.Tag as string == kind ? null : kind);
        _box.EndChange();
    }

    void InsertDivider()
    {
        if (Current is not { } p || p.Parent is not FlowDocument doc) return;
        _box.BeginChange();
        var hr = new Paragraph();
        StyleBlock(hr, "hr");
        var after = new Paragraph();
        StyleBlock(after, null);
        doc.Blocks.InsertAfter(p, hr);
        doc.Blocks.InsertAfter(hr, after);
        _box.EndChange();
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
        sel.ApplyPropertyValue(TextElement.FontFamilyProperty, on ? _box.Document.FontFamily : Mono);
        sel.ApplyPropertyValue(TextElement.BackgroundProperty, on ? null : new SolidColorBrush(Th.Ink(0x18)));
    }

    void InsertLink()
    {
        var sel = _box.Selection;
        var url = AskWindow.Ask(L.T("Link"), L.T("Adres (URL):"), "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        _box.BeginChange();
        if (sel.IsEmpty)
        {
            var run = new Run(url, sel.Start);
            var link = MakeLink(run.ContentStart, run.ContentEnd, url);
            _box.CaretPosition = link.ElementEnd;
        }
        else MakeLink(sel.Start, sel.End, url);
        _box.EndChange();
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

    /// <summary>The text of the caret's line up to the caret (across differently formatted pieces).</summary>
    string LineBeforeCaret(out TextPointer lineStart)
    {
        var caret = _box.CaretPosition;
        lineStart = caret.Paragraph?.ContentStart ?? caret.GetLineStartPosition(0) ?? caret;
        return new TextRange(lineStart, caret).Text;
    }

    /// <summary>The position <paramref name="chars"/> characters before the caret (counting text only).</summary>
    TextPointer? Back(int chars)
    {
        var p = _box.CaretPosition;
        while (chars > 0 && p != null)
        {
            var ctx = p.GetPointerContext(LogicalDirection.Backward);
            if (ctx == TextPointerContext.Text)
            {
                int n = Math.Min(chars, p.GetTextRunLength(LogicalDirection.Backward));
                p = p.GetPositionAtOffset(-n);
                chars -= n;
            }
            else if (ctx == TextPointerContext.None) return null;
            else p = p.GetNextContextPosition(LogicalDirection.Backward);
        }
        return p;
    }

    void InlineAuto()
    {
        var caret = _box.CaretPosition;
        if (caret.Paragraph is { Tag: "code" }) return;
        var before = LineBeforeCaret(out _);
        before = before[(before.LastIndexOfAny(new[] { '\r', '\n' }) + 1)..]; // never across a Shift+Enter line break
        foreach (var (re, kind) in InlineRules)
        {
            var m = re.Match(before);
            if (!m.Success) continue;
            if (Back(m.Length) is not { } start) return;
            _box.BeginChange();
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
                    _box.EndChange();
                    _box.CaretPosition = link.ElementEnd;
                    return;
            }
            // what is typed next is plain again: an empty plain piece right after it, the caret in it
            var plain = new Run("", range.End)
            {
                FontWeight = caret.Paragraph?.FontWeight ?? FontWeights.Normal, FontStyle = FontStyles.Normal,
                TextDecorations = new TextDecorationCollection(), FontFamily = _box.Document.FontFamily, Background = null,
            };
            _box.EndChange();
            _box.CaretPosition = plain.ContentStart;
            _box.Selection.Select(plain.ContentStart, plain.ContentStart);
            _box.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, caret.Paragraph?.FontWeight ?? FontWeights.Normal);
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
        link.Tag = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        return link;
    }

    // ---------- check boxes: "☐" / "☑" at the start of the line ----------

    static bool IsCheck(string? s) => s is Unchecked or Checked;

    static bool IsTodo(Paragraph p) => p.Inlines.FirstInline is Run r && IsCheck(r.Text);

    static bool IsTodoChecked(Paragraph p) => p.Inlines.FirstInline is Run { Text: Checked };

    static Run CheckRun(bool done) => new(done ? Checked : Unchecked)
    {
        FontFamily = Symbols, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal,
        Foreground = done ? Res("AccentBrush") : Res("FgDim"), TextDecorations = new TextDecorationCollection(),
    };

    void ToggleTodo()
    {
        if (Current is not { } p) return;
        _box.BeginChange();
        if (IsTodo(p)) { RemoveCheck(p); StyleBlock(p, null); }
        else MakeTodo(p, false);
        _box.EndChange();
    }

    static void MakeTodo(Paragraph p, bool done)
    {
        if (IsTodo(p)) return;
        p.Tag = "todo";
        p.Margin = new Thickness(0, 3, 0, 3);
        var check = CheckRun(done);
        var gap = new Run(" ");
        if (p.Inlines.FirstInline is { } first) { p.Inlines.InsertBefore(first, check); p.Inlines.InsertAfter(check, gap); }
        else { p.Inlines.Add(check); p.Inlines.Add(gap); }
        DimTodo(p, done);
    }

    static void SetCheck(Paragraph p, bool done)
    {
        if (p.Inlines.FirstInline is not Run r || !IsCheck(r.Text)) return;
        r.Text = done ? Checked : Unchecked;
        r.Foreground = done ? Res("AccentBrush") : Res("FgDim");
        DimTodo(p, done);
    }

    static void DimTodo(Paragraph p, bool done)
    {
        foreach (var run in Runs(p.Inlines).Skip(1))
        {
            run.TextDecorations = done ? TextDecorations.Strikethrough : new TextDecorationCollection();
            if (done) run.Foreground = Res("FgFaint"); else run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    static void RemoveCheck(Paragraph p)
    {
        if (!IsTodo(p)) return;
        DimTodo(p, false);
        p.Inlines.Remove(p.Inlines.FirstInline);
        if (p.Inlines.FirstInline is Run g && g.Text.StartsWith(' ')) { if (g.Text.Length == 1) p.Inlines.Remove(g); else g.Text = g.Text[1..]; }
        p.Tag = null;
    }

    /// <summary>A click on "☐" / "☑" ticks it.</summary>
    void OnBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = _box.GetPositionFromPoint(e.GetPosition(_box), false);
        if (pos?.Paragraph is not { } p || !IsTodo(p)) return;
        if (new TextRange(p.ContentStart, pos).Text.Length > 1) return; // not on the box
        e.Handled = true;
        _box.BeginChange();
        SetCheck(p, !IsTodoChecked(p));
        _box.EndChange();
        Flush();
    }

    /// <summary>The caret never sits before or inside the box (typing there would go in front of it).</summary>
    void KeepCaretAfterCheck()
    {
        if (_box.Selection.IsEmpty && Current is { Tag: "hr" } line && line.Parent is FlowDocument)
        {
            if (line.NextBlock is { } next) _box.CaretPosition = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward); // a list, a paragraph …
            else { var below = new Paragraph(); StyleBlock(below, null); ((FlowDocument)line.Parent).Blocks.InsertAfter(line, below); _box.CaretPosition = below.ContentStart; }
            return;
        }
        if (!_box.Selection.IsEmpty || Current is not { } p || !IsTodo(p)) return;
        var caret = _box.CaretPosition;
        if (new TextRange(p.ContentStart, caret).Text.Length >= 2) return;
        var after = p.ContentStart.GetPositionAtOffset(0);
        for (int i = 0; i < 2 && after != null; i++) after = after.GetNextInsertionPosition(LogicalDirection.Forward);
        if (after != null && after.Paragraph == p && after.CompareTo(caret) != 0) _box.CaretPosition = after;
    }

    // ---------- keys ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; ToggleStrike(); return; }
        if (e.Key == Key.E && mods == ModifierKeys.Control) { e.Handled = true; ToggleCode(); return; }
        if (e.Key == Key.K && mods == ModifierKeys.Control) { e.Handled = true; InsertLink(); return; }
        if (mods == ModifierKeys.Control && e.Key is Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4)
        {
            e.Handled = true;
            SetBlock(e.Key switch { Key.D1 => "h1", Key.D2 => "h2", Key.D3 => "h3", Key.D4 => "h4", _ => null });
            return;
        }
        if (e.Key == Key.Space && mods == ModifierKeys.None && BlockAuto()) { e.Handled = true; return; }
        if (e.Key == Key.Back && mods == ModifierKeys.None && Backspace()) { e.Handled = true; return; }
        if (e.Key == Key.Enter && mods == ModifierKeys.None && Enter()) e.Handled = true;
    }

    /// <summary>Space after "# " … "###### ", "- " "* " "+ ", "1. ", "[] " "[x] ", "> " at the start of a line.</summary>
    bool BlockAuto()
    {
        if (Current is not { } p || p.Parent is not FlowDocument || p.Tag as string == "code" || IsTodo(p)) return false;
        var before = new TextRange(p.ContentStart, _box.CaretPosition).Text;
        string? kind = before switch
        {
            "#" => "h1", "##" => "h2", "###" => "h3", "####" => "h4", "#####" => "h5", "######" => "h6", ">" => "quote",
            "-" or "*" or "+" => "bullet", "[]" or "[ ]" => "todo", "[x]" or "[X]" => "done",
            _ => Regex.IsMatch(before, @"^\d+\.$") ? "number" : null,
        };
        if (kind == null) return false;
        _box.BeginChange();
        new TextRange(p.ContentStart, _box.CaretPosition).Text = "";
        if (kind == "bullet") EditingCommands.ToggleBullets.Execute(null, _box);
        else if (kind == "number") EditingCommands.ToggleNumbering.Execute(null, _box);
        else if (kind is "todo" or "done") { MakeTodo(p, kind == "done"); _box.CaretPosition = p.ContentEnd; }
        else StyleBlock(p, kind);
        _box.EndChange();
        return true;
    }

    /// <summary>Backspace at the start of a formatted line makes it plain first (the box, a heading, a quote, code).</summary>
    bool Backspace()
    {
        if (!_box.Selection.IsEmpty || Current is not { } p || p.Parent is not FlowDocument) return false;
        int at = new TextRange(p.ContentStart, _box.CaretPosition).Text.Length;
        if (IsTodo(p) && at <= 2) { _box.BeginChange(); RemoveCheck(p); StyleBlock(p, null); _box.EndChange(); _box.CaretPosition = p.ContentStart; return true; }
        if (at == 0 && p.Tag is string) { _box.BeginChange(); StyleBlock(p, null); _box.EndChange(); return true; }
        if (at == 0 && p.PreviousBlock is Paragraph { Tag: "hr" } hr && p.Parent is FlowDocument doc) { _box.BeginChange(); doc.Blocks.Remove(hr); _box.EndChange(); return true; }
        return false;
    }

    bool Enter()
    {
        if (Current is not { } cur) return false;
        var kind = cur.Tag as string;
        bool todo = IsTodo(cur);
        var text = new TextRange(cur.ContentStart, cur.ContentEnd).Text.Trim();
        if (todo && (text == Unchecked || text == Checked)) { _box.BeginChange(); RemoveCheck(cur); StyleBlock(cur, null); _box.EndChange(); return true; } // an empty box ends the list
        if (kind == "code" && text.Length == 0) { _box.BeginChange(); StyleBlock(cur, null); _box.EndChange(); return true; } // … and the code block
        if (kind != "code" && !todo && cur.Parent is FlowDocument doc && (Regex.IsMatch(text, @"^(-{3,}|\*{3,}|_{3,})$") || text.StartsWith("```")))
        {
            bool code = text.StartsWith("```");
            _box.BeginChange();
            new TextRange(cur.ContentStart, cur.ContentEnd).Text = "";
            if (code) { StyleBlock(cur, "code"); _box.EndChange(); return true; }
            StyleBlock(cur, "hr");
            var next = new Paragraph();
            StyleBlock(next, null);
            doc.Blocks.InsertAfter(cur, next);
            _box.EndChange();
            _box.CaretPosition = next.ContentStart;
            return true;
        }
        // after the split: a heading is followed by plain text, a check box line by another (unticked) one, code by code
        Dispatcher.BeginInvoke(() =>
        {
            if (Current is not { } np || ReferenceEquals(np, cur)) return;
            _box.BeginChange();
            if (todo)
            {
                StyleBlock(np, null);
                if (IsTodo(np)) RemoveCheck(np); // split in front of the box
                DimTodo(np, false);
                foreach (var r in Runs(np.Inlines)) { r.ClearValue(TextElement.ForegroundProperty); r.TextDecorations = new TextDecorationCollection(); }
                MakeTodo(np, false);
                if (!IsTodo(cur) && new TextRange(cur.ContentStart, cur.ContentEnd).Text.Trim().Length == 0) StyleBlock(cur, null);
            }
            else if (kind is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "quote" or "hr") StyleBlock(np, null);
            else if (kind == "code") StyleBlock(np, "code");
            _box.EndChange();
            if (todo) { var c = np.ContentStart; for (int i = 0; i < 2 && c != null; i++) c = c.GetNextInsertionPosition(LogicalDirection.Forward); if (c != null) _box.CaretPosition = c; }
            else _box.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        }, DispatcherPriority.Input);
        return false;
    }

    // ---------- pasting Markdown: formatted like typed ----------

    static readonly Regex LooksLikeMd = new(@"(^|\n)\s*(#{1,6}\s|[-*+]\s|\d+\.\s|>\s|```|-{3,}\s*($|\n))|\*\*[^*]+\*\*|~~[^~]+~~|`[^`]+`|\[[^\]]+\]\([^)]+\)", RegexOptions.Compiled);

    void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = (e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "").Replace("\r", "");
        if (!LooksLikeMd.IsMatch(text))
        {
            if (e.DataObject.GetFormats().Any(f => f != DataFormats.UnicodeText && f != DataFormats.Text && f != DataFormats.OemText)) e.DataObject = new DataObject(DataFormats.UnicodeText, text);
            return;
        }
        e.CancelCommand();
        InsertMarkdown(text);
    }

    /// <summary>Puts Markdown in at the caret: the note is rebuilt around it (one Ctrl+Z step), the caret after it.</summary>
    void InsertMarkdown(string md)
    {
        const string Mark = "⁣⁣";
        _box.BeginChange();
        _box.Selection.Text = Mark;
        var whole = Serialize();
        int i = whole.IndexOf(Mark, StringComparison.Ordinal);
        if (i < 0) { _box.Selection.Text = md; _box.EndChange(); return; }
        // a pasted block starts on its own line
        bool lineStart = i == 0 || whole[i - 1] == '\n';
        var merged = whole[..i] + (lineStart || !md.Contains('\n') ? "" : "\n") + md + Mark + whole[(i + Mark.Length)..];
        _box.Document.Blocks.Clear();
        foreach (var b in Parse(merged)) _box.Document.Blocks.Add(b);
        TextPointer? caret = null;
        foreach (var run in AllRuns(_box.Document))
        {
            int at = run.Text.IndexOf(Mark, StringComparison.Ordinal);
            if (at < 0) continue;
            var s = run.ContentStart.GetPositionAtOffset(at)!;
            var end = s.GetPositionAtOffset(Mark.Length)!;
            new TextRange(s, end).Text = "";
            caret = s;
            break;
        }
        _box.EndChange();
        if (caret != null) _box.CaretPosition = caret;
        Flush();
    }

    static IEnumerable<Run> AllRuns(FlowDocument doc)
    {
        IEnumerable<Run> Of(Block b) => b switch
        {
            Paragraph p => Runs(p.Inlines),
            List l => l.ListItems.SelectMany(li => li.Blocks).SelectMany(Of),
            Section s => s.Blocks.SelectMany(Of),
            _ => Enumerable.Empty<Run>(),
        };
        return doc.Blocks.ToList().SelectMany(Of).ToList();
    }

    // ---------- the Markdown mode's buttons: they write the marks ----------

    (int Start, int End) SrcLines()
    {
        var t = _src.Text;
        int s = _src.SelectionStart, e = s + _src.SelectionLength;
        int ls = s == 0 ? 0 : t.LastIndexOf('\n', Math.Max(0, s - 1)) + 1;
        if (s > 0 && t[s - 1] == '\n') ls = s;
        int le = t.IndexOf('\n', Math.Max(e - (e > s && t[e - 1] == '\n' ? 1 : 0), 0));
        return (ls, le < 0 ? t.Length : le);
    }

    void SrcReplace(int start, int end, string with, int caretFromStart = -1)
    {
        _src.Select(start, end - start);
        _src.SelectedText = with;
        if (caretFromStart >= 0) { _src.Select(start + caretFromStart, 0); }
    }

    void SrcWrap(string mark)
    {
        var sel = _src.SelectedText;
        int s = _src.SelectionStart;
        if (sel.Length >= 2 * mark.Length && sel.StartsWith(mark) && sel.EndsWith(mark)) { SrcReplace(s, s + sel.Length, sel[mark.Length..^mark.Length]); return; }
        SrcReplace(s, s + sel.Length, mark + sel + mark, sel.Length == 0 ? mark.Length : -1);
    }

    static readonly Regex SrcPrefixRe = new(@"^(\s*)(#{1,6}\s|[-*+]\s\[[ xX]\]\s|[-*+]\s|\d+\.\s|>\s)?");

    void SrcPrefix(string prefix)
    {
        var (ls, le) = SrcLines();
        var lines = _src.Text[ls..le].Split('\n');
        // the same mark on every selected line again = take it off
        bool all = prefix.Length > 0 && lines.All(l => SrcPrefixRe.Match(l).Groups[2].Value == prefix);
        int n = 1;
        var outLines = lines.Select(l =>
        {
            var m = SrcPrefixRe.Match(l);
            var rest = l[m.Length..];
            var p = all ? "" : prefix == "1. " ? $"{n++}. " : prefix;
            return m.Groups[1].Value + p + rest;
        });
        SrcReplace(ls, le, string.Join("\n", outLines));
    }

    void SrcIndent(bool more)
    {
        var (ls, le) = SrcLines();
        var lines = _src.Text[ls..le].Split('\n').Select(l => more ? "  " + l : l.StartsWith("  ") ? l[2..] : l.TrimStart(' '));
        SrcReplace(ls, le, string.Join("\n", lines));
    }

    void SrcInsertLine(string line)
    {
        var (_, le) = SrcLines();
        SrcReplace(le, le, (le > 0 ? "\n" : "") + line + "\n", line.Length + (le > 0 ? 2 : 1));
    }

    void SrcFence()
    {
        var sel = _src.SelectedText;
        var (ls, le) = SrcLines();
        if (sel.Length == 0) { SrcReplace(le, le, (le > 0 ? "\n" : "") + "```\n\n```", le > 0 ? 5 : 4); return; }
        SrcReplace(ls, le, "```\n" + _src.Text[ls..le] + "\n```");
    }

    void SrcLink()
    {
        var url = AskWindow.Ask(L.T("Link"), L.T("Adres (URL):"), "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        var sel = _src.SelectedText;
        int s = _src.SelectionStart;
        SrcReplace(s, s + sel.Length, $"[{(sel.Length > 0 ? sel : url.Trim())}]({url.Trim()})");
    }

    void SrcStrip()
    {
        var sel = _src.SelectedText;
        if (sel.Length == 0) return;
        int s = _src.SelectionStart;
        SrcReplace(s, s + sel.Length, StripInline(sel));
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
            case Paragraph p when IsTodo(p):
                sb.Append(IsTodoChecked(p) ? "- [x] " : "- [ ] ").Append(Inlines(p.Inlines, skipFirst: true).TrimStart()).Append('\n');
                break;
            case Paragraph p:
                var kind = p.Tag as string;
                var text = Inlines(p.Inlines);
                sb.Append(kind switch
                {
                    "h1" => "# " + text, "h2" => "## " + text, "h3" => "### " + text, "h4" => "#### " + text, "h5" => "##### " + text, "h6" => "###### " + text,
                    "quote" => "> " + text, "hr" => text.Trim().Length > 0 ? "---\n" + text : "---",
                    _ => text,
                }).Append('\n');
                break;
            case List list:
                int n = list.StartIndex;
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
            case Section s:
                foreach (var inner in s.Blocks) Write(sb, inner, depth);
                break;
        }
    }

    static string Inlines(InlineCollection inlines, bool skipFirst = false)
    {
        var sb = new StringBuilder();
        foreach (var i in inlines.Skip(skipFirst ? 1 : 0))
        {
            switch (i)
            {
                case Hyperlink h: sb.Append('[').Append(Inlines(h.Inlines)).Append("](").Append(h.Tag as string ?? h.NavigateUri?.OriginalString ?? "").Append(')'); break;
                case Span s: sb.Append(Inlines(s.Inlines)); break;
                case Run r: sb.Append(Format(r)); break;
                case LineBreak: sb.Append("  \n"); break; // Markdown's hard break
            }
        }
        return sb.ToString().Replace("****", "").Replace("~~~~", "").Replace("``", "");
    }

    static string Format(Run run)
    {
        var t = run.Text;
        if (t.Length == 0) return "";
        var para = FindParagraph(run);
        if (run.FontFamily?.Source == Mono.Source && para is not { Tag: "code" }) return "`" + t + "`";
        bool bold = run.FontWeight >= FontWeights.SemiBold && para is not { Tag: "h1" or "h2" or "h3" or "h4" or "h5" or "h6" };
        bool italic = run.FontStyle == FontStyles.Italic;
        bool strike = run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true && !(para != null && IsTodoChecked(para));
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
        foreach (var i in inlines.ToList())
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
                StyleBlock(p, null);
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
                    if (lists.Count == 0) blocks.Add(list);
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

    static readonly Regex InlineFormat = new(@"\[([^\]]+)\]\(([^)\s]+)\)|`([^`]+)`|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|(?<![*\w])\*([^*\s](?:[^*]*?[^*\s])?)\*(?!\*)|(?<![_\w])_([^_\s](?:[^_]*?[^_\s])?)_(?!\w)"); // "2 * 3 * 4" stays as it is

    static void AddInlines(Paragraph p, string text)
    {
        int at = 0;
        foreach (Match m in InlineFormat.Matches(text))
        {
            if (m.Index > at) p.Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success) p.Inlines.Add(Link(new Hyperlink(new Run(m.Groups[1].Value)), m.Groups[2].Value));
            else if (m.Groups[3].Success) p.Inlines.Add(new Run(m.Groups[3].Value) { FontFamily = Mono, Background = new SolidColorBrush(Th.Ink(0x18)) });
            else if (m.Groups[4].Success || m.Groups[5].Success) p.Inlines.Add(new Run(m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value) { FontWeight = FontWeights.Bold });
            else if (m.Groups[6].Success) p.Inlines.Add(new Run(m.Groups[6].Value) { TextDecorations = TextDecorations.Strikethrough });
            else p.Inlines.Add(new Run(m.Groups[7].Success ? m.Groups[7].Value : m.Groups[8].Value) { FontStyle = FontStyles.Italic });
            at = m.Index + m.Length;
        }
        if (at < text.Length) p.Inlines.Add(new Run(text[at..]));
    }

    static string StripInline(string s) => InlineFormat.Replace(s, m =>
        m.Groups[1].Success ? $"{m.Groups[1].Value} ({m.Groups[2].Value})"
        : Enumerable.Range(3, 6).Select(i => m.Groups[i]).First(g => g.Success).Value);

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
                var h = StripInline(m.Groups[2].Value);
                sb.Append(m.Groups[1].Value.Length == 1 ? h.ToUpperInvariant() : h).Append('\n');
                if (m.Groups[1].Value.Length <= 2) sb.Append(new string(m.Groups[1].Value.Length == 1 ? '=' : '-', Math.Max(3, h.Length))).Append('\n');
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$")) { sb.Append(new string('─', 24)).Append('\n'); continue; }
            if ((m = TodoLine.Match(line)).Success) { sb.Append(new string(' ', line.Length - line.TrimStart().Length)).Append(m.Groups[1].Value is "x" or "X" ? "☑ " : "☐ ").Append(StripInline(m.Groups[2].Value)).Append('\n'); continue; }
            if ((m = ListLine.Match(line)).Success) { sb.Append(m.Groups[1].Value).Append(char.IsDigit(m.Groups[2].Value[0]) ? m.Groups[2].Value + " " : "• ").Append(StripInline(m.Groups[3].Value)).Append('\n'); continue; }
            if (line.StartsWith("> ")) { sb.Append("│ ").Append(StripInline(line[2..])).Append('\n'); continue; }
            sb.Append(StripInline(line)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---------- self-test hooks (no UI shown) ----------

    /// <summary>Round trip for the self-test.</summary>
    public static string RoundTrip(string md)
    {
        var e = new NotesEditor();
        e.Load(md, force: true);
        return e.Serialize();
    }

    /// <summary>
    /// Types into an empty note as a person would (space / Backspace / the box click run their handlers; "\b" = Backspace,
    /// "\t" = click the line's box) and returns the Markdown – for the self-test.
    /// </summary>
    public static string TypeTest(string keys, string start = "")
    {
        var e = new NotesEditor();
        e.SetMode(false, save: false);
        e.Load(start, force: true);
        e._box.CaretPosition = e._box.Document.ContentEnd;
        foreach (var ch in keys)
        {
            if (ch == ' ' && e.BlockAuto()) continue;
            if (ch == '\b')
            {
                if (e.Backspace()) continue;
                var c0 = e._box.CaretPosition;
                if (e.Back(1) is { } b0) { new TextRange(b0, c0).Text = ""; }
                continue;
            }
            if (ch == '\u0002') { if (e.Current is { } hp) e._box.CaretPosition = hp.ContentStart; e.KeepCaretAfterCheck(); continue; } // Home
            if (ch == '\t') { if (e.Current is { } tp && IsTodo(tp)) SetCheck(tp, !IsTodoChecked(tp)); continue; }
            var caret = e._box.CaretPosition;
            caret.InsertTextInRun(ch.ToString());
            e._box.CaretPosition = caret.GetPositionAtOffset(1, LogicalDirection.Forward) ?? caret;
            if (ch is '*' or '_' or '~' or '`' or ')') e.InlineAuto();
        }
        return e.Serialize();
    }
}
