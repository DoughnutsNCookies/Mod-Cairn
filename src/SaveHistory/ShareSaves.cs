using System;
using System.IO;
using HarmonyLib;
using FS = Il2CppSystem.IO;

namespace SaveHistory;

// Cairn keeps the slot you're playing open for the whole session with no read sharing,
// so nothing else can copy it until you quit. This adds read sharing to the game's own
// open of its save slot files (*.CPK under the save folder) and changes nothing else:
// the game still has full write access, and every other file it opens is untouched.
// Patched: the one FileStream constructor every path-based FileStream constructor
// ends up in. It is large and unique, so it is safe to hook (unlike tiny getters).
static class ShareSaves
{
    static int logged;

    public static void Install(Harmony harmony)
    {
        var ctor = AccessTools.Constructor(typeof(FS.FileStream), new[]
        {
            typeof(string), typeof(FS.FileMode), typeof(FS.FileAccess), typeof(FS.FileShare),
            typeof(int), typeof(bool), typeof(FS.FileOptions),
        });
        if (ctor == null) { Core.Warn("FileStream constructor not found; the slot you're playing is only copied after it closes."); return; }
        harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(ShareSaves), nameof(Prefix)));
        Core.Info("Save slot files will be opened with read sharing, so they can be copied during play.");
    }

    static void Prefix(string path, ref FS.FileShare share)
    {
        try
        {
            if (path == null || (share & FS.FileShare.Read) != 0) return;
            if (!path.EndsWith(".CPK", StringComparison.OrdinalIgnoreCase)) return;
            string root = Core.SaveRoot;
            if (root == null || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            share |= FS.FileShare.Read;
            if (logged++ < 10) Core.Info($"Allowed reading {Path.GetFileName(path)} while the game has it open");
        }
        catch { }
    }
}
