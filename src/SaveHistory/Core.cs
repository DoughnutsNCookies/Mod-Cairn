using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace SaveHistory;

// Watches the save folder and zips every changed save file into the history folder.
// Kept free of BepInEx so it can be tested outside the game.
public static class Core
{
    public static Action<string> Info = Console.WriteLine, Warn = Console.WriteLine, Error = Console.WriteLine;
    static string saveRoot, historyDir;
    static FileSystemWatcher watcher;
    static Timer debounce;
    static readonly object gate = new();
    static readonly Dictionary<string, string> lastHash = new(StringComparer.OrdinalIgnoreCase);
    const int QuietMs = 5000;  // wait until the game has stopped writing for this long
    const int RetryMs = 15000; // a file the game still holds locked is tried again this often
    static readonly HashSet<string> lockWarned = new(StringComparer.OrdinalIgnoreCase);
    public static string SaveRoot => saveRoot;

    public static void Start(string saveFolder, string historyFolder, string toolSourceDir)
    {
        saveRoot = saveFolder;
        historyDir = historyFolder;
        if (!Directory.Exists(saveRoot))
        {
            Error("Save folder not found: " + saveRoot);
            return;
        }
        Directory.CreateDirectory(historyDir);
        WriteRestoreTool(toolSourceDir);

        debounce = new Timer(_ => Snapshot("save"), null, Timeout.Infinite, Timeout.Infinite);
        // Record what is on disk right now, so the state before this session can be restored too.
        ThreadPool.QueueUserWorkItem(_ => Snapshot("game start"));

        watcher = new FileSystemWatcher(saveRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler onChange = (_, _) => debounce.Change(QuietMs, Timeout.Infinite);
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Renamed += (_, _) => debounce.Change(QuietMs, Timeout.Infinite);
        watcher.EnableRaisingEvents = true;

        Info($"Copying every save of {saveRoot} into {historyDir}");
    }

    // Zips every save file that changed since the last copy. Unchanged files are skipped.
    static void Snapshot(string reason)
    {
        lock (gate)
        {
            try
            {
                if (lastHash.Count == 0) LoadLastHashes();

                var changed = new List<(string rel, byte[] data, string hash)>();
                bool anyLocked = false;
                foreach (var f in Directory.GetFiles(saveRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(saveRoot, f);
                    byte[] data = ReadShared(f, out string why);
                    if (data == null)
                    {
                        // Copy the other files now, and try this one again shortly.
                        anyLocked = true;
                        if (lockWarned.Add(rel)) Warn($"Can't read {rel} yet ({why}); will keep retrying.");
                        continue;
                    }
                    lockWarned.Remove(rel);
                    string h = Convert.ToHexString(SHA256.HashData(data));
                    if (lastHash.TryGetValue(rel, out var old) && old == h) continue;
                    changed.Add((rel, data, h));
                }
                if (anyLocked) debounce.Change(RetryMs, Timeout.Infinite);
                if (changed.Count == 0) return;

                // Name: time, then which story slots changed, e.g. "2026-11-27_18-45-16 SLOT_B.zip"
                var slots = changed.Select(c => Path.GetFileNameWithoutExtension(c.rel))
                                   .Where(n => n.StartsWith("SLOT_", StringComparison.OrdinalIgnoreCase)).ToList();
                string label = slots.Count > 0 ? string.Join("+", slots) : "settings";
                string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss} {label}";
                string path = Path.Combine(historyDir, name + ".zip");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(historyDir, $"{name} ({i}).zip");

                string tmp = path + ".part";
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    foreach (var c in changed)
                    {
                        var e = zip.CreateEntry(c.rel.Replace('\\', '/'), CompressionLevel.Optimal);
                        using var s = e.Open();
                        s.Write(c.data, 0, c.data.Length);
                    }
                    var note = zip.CreateEntry("_snapshot.txt");
                    using var w = new StreamWriter(note.Open());
                    w.WriteLine($"reason={reason}");
                    w.WriteLine($"time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    foreach (var c in changed) w.WriteLine($"file={c.rel} sha256={c.hash}");
                }
                File.Move(tmp, path);
                foreach (var c in changed) lastHash[c.rel] = c.hash;
                Info($"Copied save ({reason}): {Path.GetFileName(path)}");
            }
            catch (Exception e) { Error("Could not copy the save: " + e); }
        }
    }

    // Rebuilds "what was copied last" from the snapshots already on disk (newest copy of each file wins).
    static void LoadLastHashes()
    {
        foreach (var zipPath in Directory.GetFiles(historyDir, "*.zip").OrderBy(p => File.GetLastWriteTimeUtc(p)))
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var note = zip.GetEntry("_snapshot.txt");
                if (note == null) continue;
                using var r = new StreamReader(note.Open());
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!line.StartsWith("file=")) continue;
                    int i = line.LastIndexOf(" sha256=", StringComparison.Ordinal);
                    if (i > 5) lastHash[line.Substring(5, i - 5)] = line.Substring(i + 8);
                }
            }
            catch (Exception e) { Warn($"Skipping unreadable snapshot {zipPath}: {e.Message}"); }
        }
    }

    static byte[] ReadShared(string f, out string why)
    {
        why = null;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException e) { why = e.Message; Thread.Sleep(500); }
        }
        return null;
    }

    // Puts the restore tool next to the copies, so it is always where the saves are.
    static void WriteRestoreTool(string dir)
    {
        try
        {
            foreach (var name in new[] { "Restore-CairnSave.ps1", "Restore Cairn Save.cmd" })
            {
                string src = Path.Combine(dir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(historyDir, name), true);
            }
        }
        catch (Exception e) { Warn("Could not place the restore tool: " + e.Message); }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string SavedGamesFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), 0, IntPtr.Zero, out var p) == 0)
            {
                string s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }
}
