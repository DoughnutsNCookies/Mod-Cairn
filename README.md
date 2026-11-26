# RecipeReveal: a Cairn mod

Cairn's cookpot normally shows a "?" for any mix you haven't cooked yet, so you end up checking a wiki. This mod makes the game's own cooking screens treat every recipe as known. Put two ingredients in the pot and the game's preview shows what they will make.

It doesn't add an overlay. It changes the game's answer to "does the player know this recipe?", so the game shows its usual preview art and names, and the journal works the same way.

> **Status: v1.0.0.** Tested on Cairn 1.1.593 (Windows): the cookpot preview shows dishes you've never cooked.

## Your save is left alone

The reveal applies only while the game is *displaying* recipes. While the game records a new discovery, cooks, or saves or loads your recipe file, the mod gives the real answer. Your save keeps its true list of discovered recipes, and cooking something new should still count as a discovery.

Back up your saves before trying it anyway. On Windows they are in `%USERPROFILE%\Saved Games\TheGameBakers\Cairn_RETAIL`.

## Install (Windows)

Find the game folder, the one with `Cairn.exe`. On Steam: right-click Cairn > Manage > Browse local files.

**Easiest: the all-in-one zip**
1. Download `RecipeReveal-vX.Y.Z-with-BepInEx.zip` from [Releases](https://github.com/DoughnutsNCookies/Mod-Cairn/releases).
2. Extract it into the game folder, so `winhttp.dll` and the `BepInEx` folder sit next to `Cairn.exe`.
3. Start the game. The first launch takes a minute or two longer while BepInEx sets itself up.

**Already have BepInEx 6 (IL2CPP)?** Download `RecipeReveal-vX.Y.Z-plugin-only.zip` and extract it into the game folder. It only adds `BepInEx\plugins\RecipeReveal\RecipeReveal.dll`.

The mod needs **BepInEx 6 bleeding edge, IL2CPP x64** (tested with `6.0.0-be.793`). BepInEx 5 and the Mono builds won't work.

**Turn it off:** in the `BepInEx\config\schuah.cairn.recipereveal.cfg` file, set `RevealAllRecipes = false`. To turn off every BepInEx mod, rename `winhttp.dll` in the game folder to `winhttp.dll.off`.

**Logs:** `BepInEx\LogOutput.log`. Lines from this mod start with `RecipeReveal`. Please attach it to any bug report.

## How it works

It patches one function, `InventoryManager.IsRecipeKnown`, to answer yes. A few large functions are guarded, and while they run the real answer comes back: `DeclareRecipeKnown`, `InventRecipe`, `ReadRecipes` and `WriteRecipes`, plus the cookpot's `OnClickedCooking`, `CookInternal` and cooking animation, and the success popup.

Small getters and lambdas are never patched. IL2CPP merges identical small functions into one shared piece of native code, so hooking one of them hooks every function that shares it, and the game crashes at startup.

## Build

You need the .NET 8 SDK and a game folder where BepInEx has been run once.

```
dotnet build src/RecipeReveal -c Release -p:GameDir="D:\path\to\Cairn"
```

The build copies the DLL into the game's `BepInEx\plugins\RecipeReveal`. Close the game first, because it locks the file while it runs. Add `-p:SkipDeploy=true` to build without copying.

## Licence

MIT. Not affiliated with The Game Bakers.
