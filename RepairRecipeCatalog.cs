using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RepairRequiresMaterials;

/// <summary>
/// Indexes recipes by their exact output prefab. Valheim's ObjectDB.GetRecipe
/// matches the shared localization name and can therefore select another mod
/// item's recipe when two outputs share a name token.
/// </summary>
internal static class RepairRecipeCatalog
{
    private static readonly IReadOnlyList<Recipe> NoRecipes = Array.Empty<Recipe>();
    private static readonly Dictionary<string, List<Recipe>> RecipesByOutput = new(StringComparer.Ordinal);

    private static ObjectDB? _indexedDatabase;
    private static List<Recipe>? _indexedRecipeList;
    private static int _indexedRecipeCount = -1;

    internal static IReadOnlyList<Recipe> GetRecipes(ItemDrop.ItemData item)
    {
        EnsureIndex();
        string key = CleanPrefabName(item?.m_dropPrefab?.name);
        return key.Length > 0 && RecipesByOutput.TryGetValue(key, out List<Recipe>? recipes)
            ? recipes
            : NoRecipes;
    }

    private static string ResolveOutputPrefabName(Recipe? recipe)
    {
        ItemDrop? output = recipe?.m_item;
        string dropPrefabName = CleanPrefabName(output?.m_itemData?.m_dropPrefab?.name);
        return dropPrefabName.Length > 0 ? dropPrefabName : CleanPrefabName(output?.name);
    }

    internal static void Invalidate()
    {
        _indexedDatabase = null;
        _indexedRecipeList = null;
        _indexedRecipeCount = -1;
        RecipesByOutput.Clear();
    }

    private static void EnsureIndex()
    {
        ObjectDB? database = ObjectDB.instance;
        List<Recipe>? recipes = database?.m_recipes;
        int recipeCount = recipes?.Count ?? 0;
        if (ReferenceEquals(_indexedDatabase, database)
            && ReferenceEquals(_indexedRecipeList, recipes)
            && _indexedRecipeCount == recipeCount)
        {
            return;
        }

        RecipesByOutput.Clear();
        _indexedDatabase = database;
        _indexedRecipeList = recipes;
        _indexedRecipeCount = recipeCount;

        if (recipes == null)
        {
            return;
        }

        foreach (Recipe recipe in recipes)
        {
            if (recipe == null)
            {
                continue;
            }

            string outputPrefabName = ResolveOutputPrefabName(recipe);
            if (outputPrefabName.Length == 0)
            {
                continue;
            }

            if (!RecipesByOutput.TryGetValue(outputPrefabName, out List<Recipe>? outputRecipes))
            {
                outputRecipes = new List<Recipe>();
                RecipesByOutput.Add(outputPrefabName, outputRecipes);
            }

            outputRecipes.Add(recipe);
        }
    }

    private static string CleanPrefabName(string? value)
    {
        string result = value?.Trim() ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        return result.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
            ? result.Substring(0, result.Length - cloneSuffix.Length).Trim()
            : result;
    }
}

[HarmonyPatch]
internal static class ObjectDBRecipeCachePatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ObjectDB), "Awake");
        yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        yield return AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        RepairRecipeCatalog.Invalidate();
    }
}
