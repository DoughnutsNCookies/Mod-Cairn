using System;
using System.IO;
using HarmonyLib;
using FS = Il2CppSystem.IO;

namespace SaveHistory;

// Cairn keeps the slot you're playing open for the whole session with no read sharing,
// so nothing else can copy it until you quit. This adds read sharing to the game's own
// open of its save slot files (*.CPK under the save folder) and changes nothing else:
// the game still has full write access, and every other file it opens is untouched.
// Patched: StandaloneDiskModule.Open and SavegameManager.OnSaved, both large game
// methods, so they are safe to hook (unlike tiny getters, which IL2CPP shares).
static class ShareSaves
{
    static int logged;

    public static void Install(Harmony harmony)
    {
        // The game's own open of save files. (1.1.0 hooked the FileStream constructor instead;
        // that never fired, so it was removed.)
        var open = AccessTools.Method(AccessTools.TypeByName("TheGameBakers.Cairn.IO.StandaloneDiskModule"), "Open",
            new[] { typeof(string), typeof(FS.FileMode), typeof(FS.FileAccess), typeof(int) });
        if (open == null) { Core.Warn("StandaloneDiskModule.Open not found; the slot you're playing is only copied after it closes."); return; }
        harmony.Patch(open, prefix: new HarmonyMethod(typeof(ShareSaves), nameof(OpenPrefix)));
        Core.Info("Save slot files will be opened with read sharing, so they can be copied during play.");

        // Copy right when the game finishes each save, so saves seconds apart each get a copy.
        var onSaved = AccessTools.Method(AccessTools.TypeByName("TheGameBakers.Cairn.Save.SavegameManager"), "OnSaved");
        if (onSaved == null) { Core.Warn("SavegameManager.OnSaved not found; saves are copied by the 15-second check only."); return; }
        harmony.Patch(onSaved, postfix: new HarmonyMethod(typeof(ShareSaves), nameof(OnSavedPostfix)));
        Core.Info("Each save is copied as soon as the game finishes it.");
    }

    static void OnSavedPostfix(bool success)
    {
        if (success) Core.CaptureNow("save");
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
}
