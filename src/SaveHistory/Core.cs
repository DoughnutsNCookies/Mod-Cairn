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
    const int QuietMs = 2000;  // wait until the game has stopped writing for this long
    const int RetryMs = 15000; // a file the game still holds locked is tried again this often
    const int PollMs = 15000;  // check for changes this often even without a change notice
    const int StableMs = 1500; // a changed file must read the same twice this far apart
    const int FastStableMs = 300; // the same, right after the game says a save finished
    static Timer poll;
    static readonly HashSet<string> lockWarned = new(StringComparer.OrdinalIgnoreCase);
    public static string SaveRoot => saveRoot;
    public static string HistoryDir => historyDir;

    // Lets MenuSlots mark a slot file it just wrote as already copied, so that file is not
    // zipped again or copied on to yet another slot.
    internal static void MarkCopied(string rel, string hash) { lock (gate) lastHash[rel] = hash; }

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

        // Windows doesn't reliably report writes to a file the game keeps open (the slot you're
        // playing), so also check every PollMs. Unchanged files cost one read and a hash.
        poll = new Timer(_ => Snapshot("save"), null, PollMs, PollMs);

        Info($"Copying every save of {saveRoot} into {historyDir}");
    }

    // Called when the game reports a finished save. Runs off the game thread: the files are
    // copied as soon as they read the same twice FastStableMs apart (the game may still be
    // flushing for a moment), which is well before a next save can start.
    public static void CaptureNow(string reason)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var changed = Collect(FastStableMs, tries: 1, out bool busy);
                    if (changed.Count > 0) { WriteZip(changed, reason); MenuSlots.CopyToMenu(changed); }
                    if (!busy) return;
                }
            }
            catch (Exception e) { Error("Could not copy the save: " + e); }
        });
    }

    // Timer / change-notice path: only keeps files that read the same twice, so a save that is
    // still being written is never kept.
    static void Snapshot(string reason)
    {
        try
        {
            var changed = Collect(StableMs, tries: 5, out bool anyLocked);
            if (anyLocked) debounce.Change(RetryMs, Timeout.Infinite);
            if (changed.Count > 0)
            {
                WriteZip(changed, reason);
                if (reason == "save") MenuSlots.CopyToMenu(changed);
            }
        }
        catch (Exception e) { Error("Could not copy the save: " + e); }
    }

    // Reads every save file and returns the ones that differ from the last copy, marking them
    // as copied so the other path doesn't copy the same state again.
    static List<(string rel, byte[] data, string hash)> Collect(int stableMs, int tries, out bool anyLocked)
    {
        anyLocked = false;
        lock (gate) { if (lastHash.Count == 0) LoadLastHashes(); }
        var result = new List<(string rel, byte[] data, string hash)>();
        foreach (var f in Directory.GetFiles(saveRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(saveRoot, f);
            byte[] data = ReadShared(f, tries, out string why);
            if (data == null)
            {
                anyLocked = true;
                lock (gate) { if (lockWarned.Add(rel)) Warn($"Can't read {rel} yet ({why}); will keep retrying."); }
                continue;
            }
            string h = Convert.ToHexString(SHA256.HashData(data));
            lock (gate)
            {
                lockWarned.Remove(rel);
                if (lastHash.TryGetValue(rel, out var old) && old == h) continue;
            }
            if (stableMs > 0)
            {
                Thread.Sleep(stableMs);
                byte[] again = ReadShared(f, tries, out _);
                if (again == null || Convert.ToHexString(SHA256.HashData(again)) != h) { anyLocked = true; continue; }
            }
            lock (gate)
            {
                if (lastHash.TryGetValue(rel, out var old) && old == h) continue; // the other path got it
                lastHash[rel] = h;
            }
            result.Add((rel, data, h));
        }
        return result;
    }

    static readonly object writeGate = new();

    // Name: time, then which story slots changed, e.g. "2026-11-27_18-45-16 SLOT_B.zip"
    static void WriteZip(List<(string rel, byte[] data, string hash)> changed, string reason)
    {
        lock (writeGate)
        {
            try
            {
                var slots = changed.Select(c => Path.GetFileNameWithoutExtension(c.rel))
                                   .Where(n => n.StartsWith("SLOT_", StringComparison.OrdinalIgnoreCase)).ToList();
                string label = slots.Count == 0 ? "settings" : slots.Count > 3 ? $"{slots.Count} slots" : string.Join("+", slots);
                string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss} {label}";
                string path = Path.Combine(historyDir, name + ".zip");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(historyDir, $"{name} ({i}).zip");

                string tmp = path + ".part";
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    foreach (var c in changed)
                    {
                        var e = zip.CreateEntry(c.rel.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
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
                Info($"Copied save ({reason}): {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                // Forget these so the next check copies them again.
                lock (gate) foreach (var c in changed) if (lastHash.TryGetValue(c.rel, out var h) && h == c.hash) lastHash.Remove(c.rel);
                Error("Could not copy the save: " + e);
            }
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

    static byte[] ReadShared(string f, int tries, out string why)
    {
        why = null;
        for (int i = 0; i < tries; i++)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException e) { why = e.Message; if (i + 1 < tries) Thread.Sleep(500); }
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
