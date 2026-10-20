using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TaskWall;

/// <summary>
/// One-click backup: every account's data (board.json, its daily copies and reports) and the settings in one ZIP.
/// Restoring merges the boards in (the newer version of each task wins) – nothing on the board is overwritten blindly.
/// </summary>
static class Backup
{
    public static string DefaultName => $"TaskWall-kopia-{DateTime.Now:yyyy-MM-dd_HHmm}.zip";

    public static void Export(string zipPath)
    {
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (layer, store) in App.OpenStores())
            {
                store.SaveNow();
                var folder = store.Folder;
                void Add(string file, string name) { if (File.Exists(file)) zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal); }
                Add(Path.Combine(folder, BoardStore.FileName), $"accounts/{layer}/{BoardStore.FileName}");
                Add(Path.Combine(folder, SharedSettings.FileName), $"accounts/{layer}/{SharedSettings.FileName}");
                foreach (var sub in new[] { "backup", "raporty" })
                {
                    var dir = Path.Combine(folder, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                        Add(f, $"accounts/{layer}/{sub}/{Path.GetFileName(f)}");
                }
            }
            // this computer's settings (the Notion token in it is encrypted for this Windows user only)
            var local = Path.Combine(SettingsStore.Dir, "settings.json");
            if (File.Exists(local)) zip.CreateEntryFromFile(local, "settings/settings.json", CompressionLevel.Optimal);
            var readme = zip.CreateEntry("CZYTAJ.txt");
            using var w = new StreamWriter(readme.Open(), new UTF8Encoding(false));
            w.WriteLine($"TaskWall {Installer.Version} – kopia z {DateTime.Now:yyyy-MM-dd HH:mm}, komputer {Environment.MachineName}.");
            w.WriteLine("Przywracanie: Ustawienia → Info → Przywróć z kopii… (zadania są scalane z tablicą, nowsza wersja wygrywa).");
        }
        File.Move(tmp, zipPath, true);
    }

    /// <summary>Merges the boards of a backup into the open accounts; returns how many accounts were merged.</summary>
    public static int Restore(string zipPath)
    {
        int merged = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var (layer, store) in App.OpenStores())
        {
            var entry = zip.GetEntry($"accounts/{layer}/{BoardStore.FileName}");
            if (entry == null) continue;
            var tmp = Path.Combine(Path.GetTempPath(), $"taskwall-restore-{layer}-{Guid.NewGuid():N}.json");
            entry.ExtractToFile(tmp, true);
            try
            {
                store.Checkpoint();
                if (store.MergeFile(tmp)) merged++;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        return merged;
    }
}
