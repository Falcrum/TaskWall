using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskWall;

/// <summary>
/// Small "today" card shown by a left click on the tray icon: today's meetings and tasks (tick them off),
/// overdue tasks and a smart quick-add box – without bringing up the whole board.
/// </summary>
public sealed class TodayWindow : Window
{
    readonly StackPanel _body = new();

    public TodayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        FontSize = 12.5;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF6, 0x16, 0x19, 0x21)),
            BorderBrush = Ui.Res("FieldBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 },
            Child = _body,
        };
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Fill();
    }

    /// <summary>Bottom-right corner above the taskbar of the primary screen (where the tray is).</summary>
    void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 4;
        Top = wa.Bottom - ActualHeight - 4;
    }

    static BoardStore Store => App.Store;

    void Changed()
    {
        App.Board?.Rebuild();
        Fill();
    }

    void Fill()
    {
        _body.Children.Clear();
        var today = DateTime.Today;
        var key = BoardWindow.DayKey(today);

        // header: DZIŚ · śr 8 paź            [PRACA]  [TABLICA]
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var account = new Button { Style = (Style)Application.Current.Resources["BarButton"], Content = L.Up(App.AccountName(App.Settings.Layer)), ToolTip = L.T("Przełącz konto") };
        account.Click += (_, _) => { App.Instance.SwitchLayer(App.Settings.Layer == "private" ? "work" : "private"); Fill(); };
        var board = new Button { Style = (Style)Application.Current.Resources["BarButton"], Content = L.T("TABLICA"), ToolTip = L.T("Pokaż tablicę na wierzchu") };
        board.Click += (_, _) => { Close(); App.Instance.SetBoardHidden(false); GlassWindow.Peek(true); App.Board?.Activate(); };
        var alarm = new Button { Style = (Style)Application.Current.Resources["BarButton"], Content = "+ ALARM", ToolTip = L.T("Nowy alarm o konkretnej godzinie") };
        alarm.Click += (_, _) => { Close(); AlarmWindow.Edit(Store); };
        DockPanel.SetDock(board, Dock.Right);
        DockPanel.SetDock(account, Dock.Right);
        DockPanel.SetDock(alarm, Dock.Right);
        head.Children.Add(board);
        head.Children.Add(account);
        head.Children.Add(alarm);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run(L.T("DZIŚ")) { FontWeight = FontWeights.Bold, FontSize = 14 });
        title.Inlines.Add(new Run("   " + today.ToString("ddd, d MMM", BoardWindow.Pl)) { Foreground = Ui.Res("FgDim"), FontSize = 12 });
        head.Children.Add(title);
        _body.Children.Add(head);

        // alarms and own meetings
        var amber = new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x4C));
        var violet = new SolidColorBrush(Color.FromRgb(0x8C, 0x9B, 0xFF));
        foreach (var a in Store.Alarms.Where(a => a.Occurs(today)).OrderBy(a => a.Time))
        {
            var col = a.IsMeeting ? violet : amber;
            bool over = (a.IsMeeting ? a.EndAt(today) : a.At(today)) <= DateTime.Now;
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Opacity = over ? 0.45 : 1, ToolTip = L.T("Kliknij, żeby zmienić") };
            t.Inlines.Add(new Run(a.IsMeeting ? "  " : "  ") { FontFamily = Ui.Font("IconFont"), FontSize = 10, Foreground = col });
            t.Inlines.Add(new Run((a.IsMeeting ? $"{a.Time}–{a.EndAt(today):HH:mm}" : a.Time) + "  ") { FontWeight = FontWeights.SemiBold, Foreground = col });
            t.Inlines.Add(new Run(a.Text.Length > 0 ? a.Text : a.IsMeeting ? L.T("Spotkanie") : "Alarm"));
            var alarmRef = a;
            t.MouseLeftButtonUp += (_, _) => { Close(); AlarmWindow.Edit(Store, alarmRef, today); };
            _body.Children.Add(t);
        }
        // meetings
        foreach (var e in CalendarService.On(today).Where(x => !x.IsHoliday))
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), Opacity = !e.AllDay && e.End < DateTime.Now ? 0.45 : 1 };
            t.Inlines.Add(new Run("  ") { FontFamily = Ui.Font("IconFont"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x9B, 0xFF)) });
            t.Inlines.Add(new Run(e.AllDay ? "" : e.Start.ToString("HH:mm") + "  ") { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x9B, 0xFF)) });
            t.Inlines.Add(new Run(e.Title));
            _body.Children.Add(t);
        }

        // tasks (scrolls when there are many)
        var tasks = Store.Data.Tasks.Where(t => t.Day == key && !t.Archived).Concat(Store.VirtualTasks(today)).OrderBy(t => t.Order).ToList();
        if (tasks.Count == 0)
            _body.Children.Add(new TextBlock { Text = L.T("Na dziś nic nie ma."), Foreground = Ui.Res("FgFaint"), Margin = new Thickness(0, 4, 0, 4) });
        var list = new StackPanel();
        _body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        foreach (var t in tasks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var circle = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.3), Cursor = Cursors.Hand,
                BorderBrush = t.Done ? Ui.Res("AccentBrush") : new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                Background = t.Done ? Ui.Res("AccentBrush") : Brushes.Transparent, Margin = new Thickness(0, 2, 9, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            circle.MouseLeftButtonUp += (_, _) =>
            {
                Store.Checkpoint();
                var real = Store.Materialize(t);
                real.Done = !real.Done;
                Store.Changed(real);
                Changed();
            };
            DockPanel.SetDock(circle, Dock.Left);
            row.Children.Add(circle);
            var text = new TextBlock { Text = t.Text, TextWrapping = TextWrapping.Wrap };
            if (t.Done) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.5; }
            if (t.Estimate is > 0)
            {
                var est = new TextBlock { Text = t.Estimate.Value.ToString("0.#", BoardWindow.Pl) + "h", Foreground = Ui.Res("FgFaint"), FontSize = 11, Margin = new Thickness(6, 1, 0, 0) };
                DockPanel.SetDock(est, Dock.Right);
                row.Children.Add(est);
            }
            row.Children.Add(text);
            list.Children.Add(row);
        }

        // overdue
        var overdue = Store.Data.Tasks.Count(t => !t.Done && !t.Archived && t.Day != null && string.CompareOrdinal(t.Day, key) < 0);
        if (overdue > 0)
        {
            var roll = new Button { Style = (Style)Application.Current.Resources["BarButton"], Content = L.F("⟲ ZALEGŁE: {0} → DZIŚ", overdue), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 4, 0, 0) };
            roll.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
            roll.Click += (_, _) => { App.Board?.RollOverdue(); Fill(); };
            _body.Children.Add(roll);
        }

        // quick add (same smart parsing as on the board: "jutro …", "2h", "#art")
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        var box = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Padding = new Thickness(8, 5, 8, 5) };
        var hint = new TextBlock { Text = L.T("dodaj na dziś…  (jutro, 2h, #art)"), Foreground = Ui.Res("FgFaint"), Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var preview = new TextBlock { FontSize = 11, Foreground = Ui.Res("AccentBrush"), Margin = new Thickness(2, 4, 0, 0) };
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            preview.Text = SmartAdd.Describe(SmartAdd.Parse(box.Text, Store.Categories));
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var r = SmartAdd.Parse(box.Text, Store.Categories);
            if (r.Text.Length == 0) return;
            var day = BoardWindow.DayKey(r.Day ?? today);
            var siblings = Store.Data.Tasks.Where(x => x.Day == day && !x.Archived);
            Store.Checkpoint();
            Store.Add(new TaskItem { Text = r.Text, Day = day, Estimate = r.Estimate, Order = siblings.Any() ? siblings.Max(x => x.Order) + 1 : 0 });
            Changed();
        };
        grid.Children.Add(box);
        grid.Children.Add(hint);
        _body.Children.Add(grid);
        _body.Children.Add(preview);
        if (IsLoaded) Dispatcher.BeginInvoke(() => box.Focus()); // after a tick / add: keep typing
        else Loaded += (_, _) => { Activate(); box.Focus(); };
    }
}
