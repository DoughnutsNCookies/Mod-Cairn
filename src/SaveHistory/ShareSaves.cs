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
        if (ctor != null) harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(ShareSaves), nameof(Prefix)));

        // The game's own open of save files. In 1.1.0 the constructor hook alone never fired
        // (the call is compiled straight through), so this is the hook that matters.
        var open = AccessTools.Method(AccessTools.TypeByName("TheGameBakers.Cairn.IO.StandaloneDiskModule"), "Open",
            new[] { typeof(string), typeof(FS.FileMode), typeof(FS.FileAccess), typeof(int) });
        if (open == null) { Core.Warn("StandaloneDiskModule.Open not found; the slot you're playing is only copied after it closes."); return; }
        harmony.Patch(open, prefix: new HarmonyMethod(typeof(ShareSaves), nameof(OpenPrefix)));
        Core.Info("Save slot files will be opened with read sharing, so they can be copied during play.");
    }

    // Opens a save slot for writing exactly as asked, except that other programs may read it.
    // Anything else, or any error, runs the game's own code unchanged.
    static bool OpenPrefix(string filePath, FS.FileMode mode, FS.FileAccess access, int bufferSize, ref FS.Stream __result)
    {
        try
        {
            if (access == FS.FileAccess.Read || !IsSlot(filePath)) return true;
            __result = new FS.FileStream(filePath, mode, access, FS.FileShare.Read, bufferSize);
            if (logged++ < 10) Core.Info($"Opened {Path.GetFileName(filePath)} ({mode}, {access}) with read sharing");
            return false;
        }
        catch (Exception e)
        {
            Core.Warn($"Read-sharing open of {filePath} failed, using the game's own: {e.Message}");
            return true;
        }
    }

    static bool IsSlot(string path)
    {
        if (path == null || !path.EndsWith(".CPK", StringComparison.OrdinalIgnoreCase)) return false;
        string root = Core.SaveRoot;
        return root != null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    static void Prefix(string path, ref FS.FileShare share)
    {
        try
        {
            if (!IsSlot(path)) return;
            if (logged < 10) Core.Info($"FileStream({Path.GetFileName(path)}, share={share})");
            if ((share & FS.FileShare.Read) != 0) return;
            share |= FS.FileShare.Read;
            if (logged++ < 10) Core.Info($"Allowed reading {Path.GetFileName(path)} while the game has it open");
        }
        catch { }
    }
}
