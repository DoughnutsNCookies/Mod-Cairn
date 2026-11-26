using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;

namespace RecipeReveal;

// Makes the game treat every recipe as known while it is only *showing* recipes
// (cookpot preview, journal), so the game's own UI tells you what a mix will make.
// While the game is recording a discovery, loading/saving, or checking achievements,
// the real answer is returned, so your save keeps its true list of discovered recipes.
[BepInPlugin("schuah.cairn.recipereveal", "RecipeReveal", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "0.1.0";

    internal static new ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;

    // Methods that answer "does the player know this recipe?"
    static readonly HashSet<string> KnownChecks = new() { "IsRecipeKnown", "get_RecipeKnown" };

    // While any of these runs, the real answer is returned (they record or count discoveries,
    // or read/write the save).
    static readonly HashSet<string> RealExact = new()
    {
        "Cook", "CookInternal", "OnCook", "CookWithItemAction", "OnCookWithItemAction", "OnCookingAction",
        "CookForFasterSimulation", "AnimateCooking", "DeclareRecipeKnown", "InventRecipe",
        "PopupCookingResult", "OnPopupCookingResult", "EatCookingResult", "OnEatCookingResult",
        "OnStoreCookingResult", "LoadRecipes", "ReadRecipes", "get_KnowsEveryRecipe", "get_KnowsHalfOfAllRecipes",
    };
    static readonly string[] RealWords =
        { "Save", "Load", "Read", "Write", "Serializ", "Achievement", "Trophy", "Stat", "Knows", "Declare", "Invent", "Unlock" };

    // Interesting names to map in the xref dump.
    static readonly string[] DumpWords = { "Recipe", "Cookpot", "Cooking", "Cook" };

    [ThreadStatic] internal static int RealDepth;
    internal static int CallLogBudget = 200;

    public override void Load()
    {
        Log = base.Log;
        Enabled = Config.Bind("General", "RevealAllRecipes", true,
            "Show every recipe as if you had already discovered it.");

        var types = GameTypes().ToList();
        Log.LogInfo($"Scanning {types.Count} game types");

        var harmony = new Harmony("schuah.cairn.recipereveal");
        var dump = new StringBuilder();
        int checks = 0, guards = 0;

        foreach (var t in types)
        {
            MethodInfo[] methods;
            try { methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }

            foreach (var m in methods)
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                string n = m.Name;
                if (n.StartsWith("NativeMethodInfoPtr") || n.StartsWith("NativeFieldInfoPtr")) continue;

                bool interesting = DumpWords.Any(w => n.Contains(w) || t.Name.Contains(w));
                if (interesting) DumpXrefs(dump, t, m);

                try
                {
                    if (KnownChecks.Contains(n) && m.ReturnType == typeof(bool))
                    {
                        harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(KnownPostfix)));
                        Log.LogInfo($"Reveal patch on {Sig(t, m)}");
                        checks++;
                    }
                    else if (IsRealGuard(t, m))
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(EnterReal)),
                            postfix: new HarmonyMethod(typeof(Plugin), nameof(ExitReal)));
                        guards++;
                    }
                }
                catch (Exception e) { Log.LogWarning($"Could not patch {Sig(t, m)}: {e.Message}"); }
            }
        }

        try
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "recipe-xrefs.txt");
            File.WriteAllText(path, dump.ToString());
            Log.LogInfo($"Wrote cooking code map to {path}");
        }
        catch (Exception e) { Log.LogWarning("Could not write xref dump: " + e.Message); }

        Log.LogInfo($"RecipeReveal {Version} loaded: {checks} known-recipe checks patched, {guards} guarded methods. Enabled={Enabled.Value}");
        if (checks == 0) Log.LogError("No IsRecipeKnown method found; nothing will be revealed.");
    }

    // Only guard methods that sit in cooking/recipe code, so unrelated save code isn't slowed.
    static bool IsRealGuard(Type t, MethodInfo m)
    {
        string n = m.Name;
        bool cookingType = DumpWords.Any(w => t.FullName.Contains(w)) || t.FullName.Contains("Inventory") || t.FullName.Contains("Journal");
        if (RealExact.Contains(n)) return true;
        if (!cookingType && !n.Contains("Recipe")) return false;
        if (RealWords.Any(w => n.Contains(w))) return true;
        // Coroutine bodies of the exact methods above, e.g. _CookInternal_d__12.MoveNext
        if (n == "MoveNext" && RealExact.Any(x => t.Name.Contains("_" + x + "_d__"))) return true;
        return false;
    }

    public static void EnterReal() => RealDepth++;
    public static void ExitReal() { if (RealDepth > 0) RealDepth--; }

    public static void KnownPostfix(MethodBase __originalMethod, ref bool __result)
    {
        bool real = __result;
        bool reveal = Enabled.Value && RealDepth == 0;
        if (reveal) __result = true;
        if (CallLogBudget > 0 && !real)
        {
            CallLogBudget--;
            Log.LogInfo($"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}: real=false -> {(reveal ? "revealed" : "kept (guarded)")}");
        }
    }

    static IEnumerable<Type> GameTypes()
    {
        var names = new List<string> { "Assembly-CSharp" };
        string interop = Path.Combine(Paths.BepInExRootPath, "interop");
        if (Directory.Exists(interop))
            names.AddRange(Directory.GetFiles(interop, "TheGameBakers.*.dll").Select(Path.GetFileNameWithoutExtension));

        foreach (var name in names.Distinct())
        {
            Assembly asm;
            try { asm = Assembly.Load(name); }
            catch (Exception e) { Log.LogWarning($"Could not load {name}: {e.Message}"); continue; }
            Type[] ts;
            try { ts = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray(); }
            foreach (var t in ts) yield return t;
        }
    }

    static void DumpXrefs(StringBuilder sb, Type t, MethodInfo m)
    {
        sb.AppendLine(Sig(t, m));
        try
        {
            foreach (var x in XrefScanner.XrefScan(m))
            {
                if (x.Type != XrefType.Method) continue;
                var target = x.TryResolve();
                if (target != null) sb.AppendLine("   calls   " + target.DeclaringType?.Name + "." + target.Name);
            }
        }
        catch (Exception e) { sb.AppendLine("   (scan failed: " + e.Message + ")"); }
        try
        {
            foreach (var x in XrefScanner.UsedBy(m))
            {
                var src = x.TryResolve();
                if (src != null) sb.AppendLine("   usedby  " + src.DeclaringType?.Name + "." + src.Name);
            }
        }
        catch { }
    }

    static string Sig(Type t, MethodInfo m) =>
        $"{t.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) : {m.ReturnType.Name}";
}
