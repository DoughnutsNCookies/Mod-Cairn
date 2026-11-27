using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SaveHistory;

// Keeps a copy of every save the game writes, so you can go back to any of them later
// with "Restore Cairn Save.cmd". It only watches the save folder from the outside: no game
// code is patched. Copies are never deleted.
[BepInPlugin("schuah.cairn.savehistory", "SaveHistory", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "1.1.0";

    public override void Load()
    {
        Core.Info = s => Log.LogInfo(s);
        Core.Warn = s => Log.LogWarning(s);
        Core.Error = s => Log.LogError(s);
        string games = Path.Combine(Core.SavedGamesFolder(), "TheGameBakers");
        Core.Start(Path.Combine(games, "Cairn_RETAIL"), Path.Combine(games, "Cairn_SaveHistory"),
                   Path.GetDirectoryName(typeof(Plugin).Assembly.Location));
        try { ShareSaves.Install(new Harmony("schuah.cairn.savehistory")); }
        catch (System.Exception e) { Log.LogError("Read-sharing patch failed; the slot you're playing is copied only after it closes: " + e); }
        Log.LogInfo($"SaveHistory {Version} loaded");
    }
}
