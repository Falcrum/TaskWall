using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// UI language. Polish text is the key: <c>L.T("Dodaj zadanie")</c> returns the English text when English is on
/// (and the Polish one when a translation is missing). <c>L.F("Zrobione {0} z {1}", a, b)</c> translates the template
/// first. The language is set once at start-up (changing it restarts the app). Translations live in En.*.cs files.
/// </summary>
static partial class L
{
    public static bool En { get; private set; }
    static readonly CultureInfo Polish = new("pl-PL");
    static readonly CultureInfo English = new("en-GB"); // Monday first, 24 h, "8 October"

    /// <summary>Culture for dates and numbers in the UI.</summary>
    public static CultureInfo Culture => En ? English : Polish;

    static Dictionary<string, string>? _dict;

    public static void Set(string language)
    {
        En = language == "en";
        _dict = null;
    }

    static Dictionary<string, string> Dict
    {
        get
        {
            if (_dict != null) return _dict;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var table in new[] { Board, Features, Settings, Dialogs, Common })
                foreach (var (pl, en) in table) d[pl] = en;
            return _dict = d;
        }
    }

    public static string T(string pl) => En && Dict.TryGetValue(pl, out var en) ? en : pl;

    public static string F(string pl, params object?[] args) => string.Format(Culture, T(pl), args);

    /// <summary>Upper case the way the language does it (PAŹDZIERNIK / OCTOBER).</summary>
    public static string Up(string s) => s.ToUpper(Culture);

    /// <summary>CZWARTEK 8 PAŹDZIERNIK / THURSDAY 8 OCTOBER (month in the nominative).</summary>
    public static string LongDay(DateTime d) =>
        Up($"{Culture.DateTimeFormat.GetDayName(d.DayOfWeek)} {d.Day} {Culture.DateTimeFormat.MonthNames[d.Month - 1]}");

    /// <summary>Translates the static texts of a XAML tree (buttons, text blocks, tool tips) in place.</summary>
    public static void Tree(DependencyObject root)
    {
        if (!En) return;
        Walk(root);
    }

    static void Walk(DependencyObject o)
    {
        switch (o)
        {
            case TextBlock tb when tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text): tb.Text = T(tb.Text); break;
            case ContentControl cc when cc.Content is string s: cc.Content = T(s); break;
        }
        if (o is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = T(tip);
        if (o is TextBox) return;
        foreach (var child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) Walk(d);
    }
}
