using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DeskWall;

/// <summary>
/// Per-user installer built into the exe (no admin rights, no extra tools):
///   DeskWall-Setup.exe (or --install)  → copies itself to %LOCALAPPDATA%\Programs\DeskWall, Start-menu shortcut,
///                                        entry in "Apps &amp; features", optional autostart, starts the installed copy.
///   --uninstall (from "Apps &amp; features") → removes program, shortcut, entries; data folders are never touched.
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskWall";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeskWall");
    public static string InstalledExe => Path.Combine(InstallDir, "DeskWall.exe");
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeskWall.lnk");

    public static bool IsInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k != null && File.Exists(InstalledExe);
        }
    }

    static bool RunningInstalled => string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handles the installer switches before the app starts. True = the process should exit.</summary>
    public static bool HandleCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--uninstall")) { Uninstall(null); return true; }
        bool setup = args.Contains("--install") || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);
        if (!setup) return false;
        if (args.Contains("--quiet")) // silent install (scripts / tests): keep the current autostart choice
        {
            Install(SettingsStore.Load().StartWithWindows);
            StartInstalled();
            return true;
        }
        var dlg = new SetupWindow();
        dlg.ShowDialog();
        return true;
    }

    /// <summary>"Zainstaluj w systemie" from the settings of a portable copy.</summary>
    public static void InstallFromRunningCopy(Window owner)
    {
        if (MessageBox.Show(owner, $"Zainstalować DeskWall {Version} w {InstallDir}?\nTa kopia zostanie zamknięta i uruchomi się zainstalowana.", "DeskWall",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Install(App.Settings.StartWithWindows);
            StartInstalled();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Fail(owner, ex); }
    }

    static void Install(bool autostart)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Nie znam ścieżki programu.");
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, InstalledExe, overwrite: true);

        CreateShortcut(ShortcutPath, InstalledExe);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "DeskWall");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Kamil Falenta");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd"));
        }
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (autostart) run?.SetValue("DeskWall", $"\"{InstalledExe}\"");
            else run?.DeleteValue("DeskWall", false);
        }
        // the installed copy reads this on start
        var s = SettingsStore.Load();
        s.StartWithWindows = autostart;
        SettingsStore.Save(s);
    }

    static void StartInstalled() =>
        Process.Start(new ProcessStartInfo(InstalledExe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });

    /// <summary>Removes program, shortcut, autostart and the "Apps &amp; features" entry. Data folders stay.</summary>
    public static void Uninstall(Window? owner)
    {
        bool quiet = Environment.GetCommandLineArgs().Contains("--quiet");
        if (!quiet && MessageBox.Show("Odinstalować DeskWall?\n\nZadania zostają w folderach kont (np. na Google Drive) – nic z nich nie jest usuwane.", "DeskWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool removeSettings = !quiet && MessageBox.Show("Usunąć też ustawienia tego komputera (%APPDATA%\\DeskWall)?\nWybierz „Nie”, jeśli zainstalujesz DeskWall ponownie.", "DeskWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            StopOtherInstances();
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue("DeskWall", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            if (removeSettings && Directory.Exists(SettingsStore.Dir))
                foreach (var f in new[] { "settings.json", "settings.json.bad", "calendar-cache.json", "error.log" })
                    try { File.Delete(Path.Combine(SettingsStore.Dir, f)); } catch { }
            // the running exe can't delete itself: a hidden shell removes the folder a moment after we exit
            if (Directory.Exists(InstallDir))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!quiet) MessageBox.Show("DeskWall został odinstalowany.", "DeskWall", MessageBoxButton.OK, MessageBoxImage.Information);
            if (owner != null) Application.Current.Shutdown();
        }
        catch (Exception ex) { Fail(owner, ex); }
    }

    static void StopOtherInstances()
    {
        foreach (var p in Process.GetProcessesByName("DeskWall").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(1500)) p.Kill(); p.WaitForExit(3000); }
            catch { /* already gone */ }
        }
    }

    /// <summary>.lnk through the Windows Script Host COM object (no extra libraries).</summary>
    static void CreateShortcut(string lnk, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Brak WScript.Shell");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "DeskWall – tablica zadań na pulpicie";
            sc.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    static void Fail(Window? owner, Exception ex)
    {
        Log.Error("installer", ex);
        if (owner != null) MessageBox.Show(owner, "Nie udało się: " + ex.Message, "DeskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show("Nie udało się: " + ex.Message, "DeskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>The small dark setup dialog shown by DeskWall-Setup.exe.</summary>
    sealed class SetupWindow : DarkWindow
    {
        public SetupWindow()
        {
            Title = "DeskWall – instalacja";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            Content = p;
            p.Children.Add(Label($"DeskWall {Version}", 20, "Fg", FontWeights.SemiBold));
            p.Children.Add(Label("Tablica zadań na pulpicie: tydzień, miesiąc i rok, backlog z Notion, kalendarz Google, synchronizacja przez chmurę.", 12, "FgDim"));
            var where = Label($"Zostanie zainstalowany w {InstallDir} (tylko dla Ciebie, bez uprawnień administratora), ze skrótem w menu Start i wpisem w „Aplikacje i funkcje”." +
                              (IsInstalled ? "\nWykryto wcześniejszą instalację – zostanie zaktualizowana, ustawienia i dane zostają." : ""), 11.5, "FgFaint");
            where.Margin = new Thickness(0, 12, 0, 12);
            p.Children.Add(where);
            var autostart = new CheckBox { Content = "Uruchamiaj razem z Windows", IsChecked = true, Margin = new Thickness(0, 0, 0, 16) };
            p.Children.Add(autostart);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Btn("Anuluj", "SecondaryButton", (_, _) => Close()));
            buttons.Children.Add(Btn(IsInstalled ? "Aktualizuj" : "Zainstaluj", "PrimaryButton", (_, _) =>
            {
                try
                {
                    Install(autostart.IsChecked == true);
                    StartInstalled();
                    Close();
                }
                catch (Exception ex) { Fail(this, ex); }
            }));
            p.Children.Add(buttons);
        }
    }
}
