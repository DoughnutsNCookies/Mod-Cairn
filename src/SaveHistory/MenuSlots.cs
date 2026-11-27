using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaveHistory;

// Puts a copy of each save into a free slot of the game's own save menu, so recent saves can
// be previewed and loaded from inside Cairn. The game reads slots A to L (12), so the menu holds
// the slot you're playing plus up to 11 copies. Older saves stay in the zip history.
//
// Safety rules:
// - Never writes the slot that was just saved (the one you're playing).
// - Only ever replaces a slot this mod created itself, and only while that file is exactly
//   what the mod wrote. If you load a copy and keep playing in it, it is a real save from then
//   on and is never replaced.
// - Writes the new slot file next to it first, then renames it into place in one step, so the
//   game can never read a half-written slot.
public static class MenuSlots
{
    const string Letters = "ABCDEFGHIJKL"; // the slots Cairn reads
    const string ManifestName = "menu-slots.txt"; // in the history folder: letter, sha256, time
    static readonly object gate = new();

    public static void CopyToMenu(List<(string rel, byte[] data, string hash)> changed)
    {
        lock (gate)
        {
            foreach (var c in changed)
            {
                string file = Path.GetFileName(c.rel);
                if (!file.StartsWith("SLOT_", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".CPK", StringComparison.OrdinalIgnoreCase)) continue;
                try { CopyOne(c.rel, c.data, c.hash); }
                catch (Exception e) { Core.Error($"Could not add {file} to the game menu: {e.Message}"); }
            }
        }
    }

    static void CopyOne(string rel, byte[] data, string hash)
    {
        string storyDir = Path.GetDirectoryName(Path.Combine(Core.SaveRoot, rel));
        char source = char.ToUpperInvariant(Path.GetFileNameWithoutExtension(rel)[5]);
        var manifest = ReadManifest();

        // Don't make a second copy of a save that is already in the menu.
        foreach (var m in manifest)
            if (m.hash == hash && File.Exists(SlotPath(storyDir, m.letter))) return;

        char target = '\0';
        foreach (char l in Letters)
            if (l != source && !File.Exists(SlotPath(storyDir, l))) { target = l; break; }

        if (target == '\0')
        {
            // Menu full: replace the oldest copy made by this mod that is still untouched.
            foreach (var m in manifest.OrderBy(m => m.time))
            {
                if (m.letter == source) continue;
                string p = SlotPath(storyDir, m.letter);
                if (File.Exists(p) && FileHash(p) == m.hash) { target = m.letter; break; }
            }
        }
        if (target == '\0')
        {
            Core.Warn("The game menu has no free slot and no untouched copy to replace; this save is in the zip history only.");
            return;
        }

        string dest = SlotPath(storyDir, target);
        string tmp = Path.Combine(Core.HistoryDir, $"_incoming_SLOT_{target}.CPK"); // same drive, outside the game's folder
        File.WriteAllBytes(tmp, data);
        string destRel = Path.GetRelativePath(Core.SaveRoot, dest);
        Core.MarkCopied(destRel, hash); // before it appears, so it isn't zipped again or copied on
        File.Move(tmp, dest, true);

        manifest.RemoveAll(m => m.letter == target);
        manifest.Add((target, hash, DateTime.Now));
        WriteManifest(manifest);
        Core.Info($"Added this save to the game menu as slot {target} (copy of slot {source})");
    }

    static string SlotPath(string storyDir, char letter) => Path.Combine(storyDir, $"SLOT_{letter}.CPK");

    static string FileHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static List<(char letter, string hash, DateTime time)> ReadManifest()
    {
        var list = new List<(char, string, DateTime)>();
        string p = Path.Combine(Core.HistoryDir, ManifestName);
        if (!File.Exists(p)) return list;
        foreach (var line in File.ReadAllLines(p))
        {
            var parts = line.Split('\t');
            if (parts.Length == 3 && parts[0].Length == 1 && DateTime.TryParse(parts[2], out var t))
                list.Add((parts[0][0], parts[1], t));
        }
        return list;
    }

    static void WriteManifest(List<(char letter, string hash, DateTime time)> list)
    {
        string p = Path.Combine(Core.HistoryDir, ManifestName);
        File.WriteAllLines(p + ".tmp", list.OrderBy(m => m.letter).Select(m => $"{m.letter}\t{m.hash}\t{m.time:yyyy-MM-dd HH:mm:ss}"));
        File.Move(p + ".tmp", p, true);
    }
}
