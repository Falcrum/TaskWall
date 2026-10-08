namespace TaskWall;

static partial class L
{
    /// <summary>Polish → English (Common).</summary>
    static readonly (string Pl, string En)[] Common =
    {
        // repeat / series labels (Models)
        ("Jednorazowo", "Once"),
        ("Codziennie", "Daily"),
        ("W dni robocze", "On weekdays"),
        ("Co tydzień", "Weekly"),
        ("Co 2 tygodnie", "Every 2 weeks"),
        ("Co miesiąc", "Monthly"),
        ("Co rok", "Yearly"),
        // RecurringRule.Describe
        ("codziennie", "daily"),
        ("w dni robocze", "on weekdays"),
        ("co tydzień", "weekly"),
        ("co 2 tygodnie", "every 2 weeks"),
        ("co miesiąc", "monthly"),
        ("co rok", "yearly"),
        ("od {0}", "from {0}"),
        ("do {0}", "until {0}"),
        // accounts
        ("Praca", "Work"),
        ("Prywatne", "Private"),
        // smart add preview
        ("dziś", "today"),
        ("jutro", "tomorrow"),
        // calendar
        ("cały dzień", "all day"),
        ("(bez tytułu)", "(no title)"),
        // Notion
        ("Notion nie jest skonfigurowany.", "Notion is not set up."),
        ("Synchronizacja już trwa.", "Sync is already running."),
        ("stron {0}, nowe {1}, zmienione {2}", "{0} pages, {1} new, {2} changed"),
        ("(nie udało się ustalić „moich”)", "(couldn't tell which ones are “mine”)"),
        ("Brak połączenia z Notion.", "Can't connect to Notion."),
        ("błąd – {0}", "error – {0}"),
        ("Brak tokenu (albo został zapisany na innym koncie Windows) – wklej go ponownie.", "No token (or it was saved under another Windows account) – paste it again."),
        ("Nie rozpoznaję linku do bazy. Skopiuj link do widoku bazy (••• → Copy link to view).", "Can't recognise the database link. Copy the link to the database view (••• → Copy link to view)."),
        ("Notion nie przyjął tokenu (wygasł albo jest błędny).", "Notion rejected the token (it has expired or is wrong)."),
        ("Nie widzę tej bazy. Sprawdź link i to, czy masz do niej dostęp w Notion na koncie, z którego jest token.", "Can't see this database. Check the link and that the Notion account the token comes from has access to it."),
        ("W archiwum ZIP nie ma pliku CSV.", "The ZIP archive contains no CSV file."),
    };
}
