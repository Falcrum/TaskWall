using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>Every smart-add command and shortcut in one place (⋯ → Komendy i skróty, F1).</summary>
public sealed class HelpWindow : DarkWindow
{
    static HelpWindow? _open;

    static readonly (string Section, (string Example, string What)[] Rows)[] Sections =
    {
        ("Nowe zadanie – sprytne dodawanie", new[]
        {
            ("jutro · pojutrze · dziś", "dzień (na początku albo na końcu tekstu)"),
            ("pt · w pt · piątek", "najbliższy taki dzień tygodnia"),
            ("za 3 dni · za tydzień", "za tyle dni"),
            ("14.10 · 14.10.2026", "konkretna data"),
            ("tomorrow · fri · in 3 days · next week", "to samo po angielsku"),
            ("2h · 1,5h · 30 min · 1h 30m", "estymacja (czas pracy)"),
            ("14-16 · 9:30–11 · od 14 do 16", "godziny od–do (bez estymacji liczy się z nich)"),
            ("o 10 · 10:30 · at 10", "godzina rozpoczęcia"),
            ("!!! · !! · !", "priorytet: wysoki · średni · niski"),
            ("#art · [ART]", "kategoria (klik w etykietę pod polem robi to samo)"),
            ("[Meeting] 14-15:30 Sprint", "spotkanie zamiast zadania (okno spotkania: klik w Meeting)"),
            ("Przykład: jutro Raport 14-16 !! #art", "jutro, 14:00–16:00 (2h), średni priorytet, kategoria ART"),
        }),
        ("Alarm i spotkanie", new[]
        {
            ("15:30 · 9 · o 7 · at 7 · 7pm", "godzina alarmu"),
            ("za 20 min · za 2 h · in 20 min", "alarm za tyle czasu"),
            ("14-15:30 · do: 1h · 90 min", "spotkanie od–do albo czas trwania"),
        }),
        ("Skróty klawiszowe", new[]
        {
            ("Ctrl+Shift+Spacja", "tablica na wierzch + nowe zadanie na dziś (Esc chowa); zmienisz w Ustawienia → Ogólne"),
            ("Ctrl+1 · Ctrl+2", "konto Praca · Prywatne"),
            ("Ctrl+F", "szukaj wszędzie"),
            ("Ctrl+Z", "cofnij"),
            ("Enter · Esc", "dodaj i następne · zamknij pole"),
            ("Esc", "zamknij panel / wróć do tygodnia / schowaj tablicę"),
            ("F1", "ta lista"),
        }),
        ("Mysz", new[]
        {
            ("klik w kółko", "zrobione / niezrobione"),
            ("klik w tekst", "edycja (zadanie z Notion: otwiera stronę)"),
            ("prawy przycisk na zadaniu", "menu: estymacja, priorytet, kategoria, powtarzanie, lista kontrolna, archiwum"),
            ("prawy przycisk na dniu", "oznaczenia dni, powtarzane oznaczenia, alarm"),
            ("przeciągnij", "na dzień, w liście, na BACKLOG lub ARCHIWUM; z Ctrl = kopia"),
            ("środkowy przycisk", "archiwizuj zadanie"),
            ("↷ wyszarzone zadanie", "kontynuowane: było tu w toku i przeszło na inny dzień (klik = idź tam)"),
            ("ikona w zasobniku", "lewy: karta Dziś · prawy: menu"),
        }),
    };

    HelpWindow()
    {
        Title = L.T("Komendy i skróty");
        Width = 720;
        Height = 760;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        var p = new StackPanel { Margin = new Thickness(22, 16, 22, 20) };
        foreach (var (section, rows) in Sections)
        {
            var head = Label(L.Up(L.T(section)), 11.5, "FgDim", FontWeights.Bold);
            head.Margin = new Thickness(0, 14, 0, 6);
            p.Children.Add(head);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            int r = 0;
            foreach (var (example, what) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var ex = new TextBlock
                {
                    Text = L.T(example), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Ui.Res("AccentBrush"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3),
                };
                var wt = Label(L.T(what), 12.5, "Fg");
                wt.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(ex, r); Grid.SetRow(wt, r); Grid.SetColumn(wt, 1);
                grid.Children.Add(ex);
                grid.Children.Add(wt);
                r++;
            }
            p.Children.Add(grid);
        }
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.F1) Close(); };
        Closed += (_, _) => _open = null;
    }

    public static void ShowHelp()
    {
        GlassWindow.EndOverlays();
        if (_open == null) { _open = new HelpWindow(); _open.Show(); }
        _open.Activate();
    }
}
