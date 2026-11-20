using System.Text.RegularExpressions;

namespace TaskWall;

/// <summary>The pure part of the dock's notification counters (tested on its own): the number a window's title shows.</summary>
public static class BadgeLogic
{
    static readonly Regex TitleStart = new(@"^\((\d{1,3})\)\s", RegexOptions.Compiled);
    static readonly Regex TitleEnd = new(@"\s\((\d{1,3})\)$", RegexOptions.Compiled);
    /// <summary>Slack: "general (Channel) - Workspace - 2 new items - Slack" (the workspace's name may hold a dot, so before the file check).</summary>
    static readonly Regex NewItems = new(@"\b(\d{1,3}) (new items?|new messages?|nowe? (elementy|element|wiadomości|wiadomość))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>A file's name in a title ("Budżet.xlsx", "Intro.mp4"): its "(2)" is a copy's number or a year, not messages.</summary>
    static readonly Regex FileName = new(@"\.[A-Za-z0-9]{1,5}(\s|$)", RegexOptions.Compiled);

    /// <summary>"(3) Messenger", "Telegram (12)", "… - 2 new items - Slack": the number a window's title shows, or 0 (never from a file's name).</summary>
    public static int TitleCount(string title)
    {
        if (NewItems.Match(title) is { Success: true } items && int.TryParse(items.Groups[1].Value, out var count)) return count;
        if (FileName.IsMatch(title)) return 0;
        var m = TitleStart.Match(title);
        if (!m.Success) m = TitleEnd.Match(title);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }
}
