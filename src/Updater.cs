using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// Updates through the cloud folder (next to board.json of the first account): the computer with the newest version
/// leaves its installer there as TaskWall-Setup.exe; the others notice it on start (and every few hours) and offer to
/// update. No server, no internet download – the same Google Drive / OneDrive that syncs the tasks.
/// </summary>
static class Updater
{
    public const string FileName = "TaskWall-Setup.exe";
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromHours(6) };
    static bool _asking;

    public static Version Mine => typeof(App).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Where the shared installer lives (the first account's data folder).</summary>
    public static string? Folder => App.Settings.Accounts.FirstOrDefault()?.Folder is { Length: > 0 } f ? f : null;
    public static string? SetupPath => Folder is { } f ? Path.Combine(f, FileName) : null;

    public static Version? VersionOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(v, out var ver) ? ver : null;
        }
        catch { return null; }
    }

    public static void Start()
    {
        if (Dev.Arg("--data") != null) return; // dev runs never update or publish
        Timer.Tick += (_, _) => Check(askAgain: false);
        Timer.Start();
        // a moment after start: the cloud drive may still be mounting
        var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        once.Tick += (_, _) => { once.Stop(); Check(askAgain: false); };
        once.Start();
    }

    /// <summary>Newer installer in the folder → offer it; ours newer → put ours there.</summary>
    public static async void Check(bool askAgain)
    {
        var setup = SetupPath;
        if (setup == null || _asking) return;
        var theirs = await Task.Run(() => VersionOf(setup));
        if (theirs != null && theirs > Mine)
        {
            if (!askAgain && App.Settings.SkippedUpdate == theirs.ToString()) return;
            _asking = true;
            try { Offer(setup, theirs); }
            finally { _asking = false; }
        }
        else if (theirs == null || theirs < Mine)
            await Publish(setup);
        else if (askAgain)
            MessageBox.Show(L.F("Masz najnowszą wersję ({0}).", Mine.ToString(3)), "TaskWall");
    }

    static void Offer(string setup, Version theirs)
    {
        GlassWindow.EndOverlays();
        var answer = MessageBox.Show(
            L.F("Jest nowsza wersja TaskWall: {0} (masz {1}).\nInstalator czeka w folderze danych:\n{2}\n\nZaktualizować teraz? Program uruchomi się ponownie, zadania i ustawienia zostają.", theirs.ToString(3), Mine.ToString(3), setup),
            "TaskWall", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            App.Settings.SkippedUpdate = theirs.ToString(); // don't ask again for this version (Settings → Info can still update)
            SettingsStore.Save(App.Settings);
            return;
        }
        _ = Install(setup);
    }

    /// <summary>Copies the installer out of the cloud folder and runs it silently (it closes this copy and starts the new one).</summary>
    public static async Task Install(string setup)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "TaskWall-Setup-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
            await Task.Run(() => File.Copy(setup, temp, true));
            Process.Start(new ProcessStartInfo(temp, "--install --quiet") { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            MessageBox.Show(L.T("Nie udało się uruchomić aktualizacji:") + "\n" + ex.Message, "TaskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Leaves this version's installer in the folder for the other computers – only from an installed, self-contained
    /// copy (the installed exe is the installer itself), never from a dev build or the small .NET-dependent exe.
    /// </summary>
    static async Task Publish(string setup)
    {
        try
        {
            var me = Environment.ProcessPath;
            if (me == null || !Installer.IsRunningInstalled || new FileInfo(me).Length < 50_000_000) return;
            await Task.Run(() =>
            {
                var tmp = setup + ".tmp"; // cloud clients skip *.tmp until it's renamed
                File.Copy(me, tmp, true);
                File.Move(tmp, setup, true);
            });
            Log.Error($"published installer {Mine} to {setup}");
        }
        catch (Exception ex) { Log.Error("publish installer", ex); }
    }
}
