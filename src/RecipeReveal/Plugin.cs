using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace RecipeReveal;

// Makes the game treat every recipe as known while it is only *showing* recipes
// (cookpot preview, journal), so the game's own UI tells you what a mix will make.
// While the game is recording a discovery, loading/saving, or checking achievements,
// the real answer is returned, so your save keeps its true list of discovered recipes.
[BepInPlugin("schuah.cairn.recipereveal", "RecipeReveal", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "0.2.1";

    internal static new ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;

    // The method that answers "does the player know this recipe?". The journal's
    // get_RecipeKnown getter is left alone: IL2CPP folds tiny getters and lambdas into one
    // shared native body, so hooking one hooks many unrelated ones (that crashed v0.1/v0.2).
    // Only large, unique methods are patched here.
    static readonly HashSet<string> KnownChecks = new() { "IsRecipeKnown" };

    // While any of these runs, the real answer is returned: they record discoveries,
    // cook, or read/write the recipe save. Keyed by the game's full type name.
    static readonly Dictionary<string, string[]> RealGuards = new()
    {
        ["TheGameBakers.Cairn.InventoryManager"] = new[] { "DeclareRecipeKnown", "InventRecipe", "ReadRecipes", "WriteRecipes" },
        ["TheGameBakers.Cairn.UI.CookingSubmenu"] = new[] { "OnClickedCooking", "CookInternal" },
        ["TheGameBakers.Cairn.UI.CookingSubmenu+_AnimateCooking_d__48"] = new[] { "MoveNext" },
        ["CookingSuccessPopup"] = new[] { "Display" },
    };

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
                        Log.LogInfo($"Guard on {Sig(t, m)}");
                        guards++;
                    }
                }
                catch (Exception e) { Log.LogWarning($"Could not patch {Sig(t, m)}: {e.Message}"); }
            }
        }

        Log.LogInfo($"RecipeReveal {Version} loaded: {checks} known-recipe checks patched, {guards} guarded methods. Enabled={Enabled.Value}");
        if (checks == 0) Log.LogError("No IsRecipeKnown method found; nothing will be revealed.");
    }

    static bool IsRealGuard(Type t, MethodInfo m) =>
        RealGuards.TryGetValue(t.FullName ?? "", out var names) && names.Contains(m.Name);

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

    static string Sig(Type t, MethodInfo m) =>
        $"{t.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) : {m.ReturnType.Name}";
}
