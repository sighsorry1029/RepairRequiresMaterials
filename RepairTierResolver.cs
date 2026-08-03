using System;
using System.Collections.Generic;

namespace RepairRequiresMaterials;

internal enum RepairBiome
{
    Meadows,
    BlackForest,
    Swamp,
    Ocean,
    Mountain,
    Plains,
    Mistlands,
    AshLands,
    DeepNorth
}

/// <summary>
/// Resolves a repairable item's progression biome from its crafting recipe.
/// The built-in material table mirrors VES's default resource map, but remains
/// independent from VES and YAML so it is available as soon as ObjectDB is ready.
/// </summary>
internal static class RepairTierResolver
{
    private const string PowderPrefabPrefix = "RRM_RepairPowder_";

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, RepairBiome> BuiltInIngredientBiomes = BuildBuiltInIngredientMap();
    private static readonly Dictionary<string, RepairBiome?> ResolutionCache = new(StringComparer.Ordinal);

    private static Dictionary<string, RepairBiome> _itemOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, RepairBiome> _ingredientOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static int _recipeCatalogRevision = -1;

    /// <summary>
    /// Resolves an item using its active ObjectDB recipe.
    /// </summary>
    internal static bool TryResolve(ItemDrop.ItemData? item, out RepairBiome biome)
    {
        biome = default;
        if (item == null)
        {
            return false;
        }

        Recipe? recipe = ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
        return TryResolve(item, recipe, out biome);
    }

    /// <summary>
    /// Resolves an item using the supplied recipe. Explicit item overrides are
    /// evaluated before recipe ingredients. Among mapped ingredients, the
    /// highest progression rank wins; unknown ingredients do not invalidate a
    /// recipe that contains at least one known ingredient.
    /// </summary>
    internal static bool TryResolve(ItemDrop.ItemData? item, Recipe? recipe, out RepairBiome biome)
    {
        biome = default;
        if (item == null)
        {
            return false;
        }

        List<string> outputTokens = GetOutputTokens(item);
        string stablePrefabName = GetStablePrefabName(item, recipe);
        string cacheKey = CleanPrefabName(stablePrefabName);
        int recipeCatalogRevision = RepairRecipeCatalog.Revision;

        lock (SyncRoot)
        {
            if (_recipeCatalogRevision != recipeCatalogRevision)
            {
                ResolutionCache.Clear();
                _recipeCatalogRevision = recipeCatalogRevision;
            }

            if (TryResolveAnyToken(outputTokens, _itemOverrides, out biome))
            {
                return true;
            }

            if (cacheKey.Length > 0 && ResolutionCache.TryGetValue(cacheKey, out RepairBiome? cached))
            {
                if (cached.HasValue)
                {
                    biome = cached.Value;
                    return true;
                }

                return false;
            }

            bool resolved = TryResolveOutputRecipes(stablePrefabName, recipe, out biome);
            // A missing recipe commonly means ObjectDB has not finished
            // registering yet. Do not make that transient state a negative
            // cache entry; callers can resolve again once the recipe exists.
            if (cacheKey.Length > 0 && recipe != null)
            {
                ResolutionCache[cacheKey] = resolved ? biome : null;
            }

            return resolved;
        }
    }

    /// <summary>
    /// Resolves a recipe directly. If an output item is available, its explicit
    /// item override is still honored before ingredient inference.
    /// </summary>
    internal static bool TryResolve(Recipe? recipe, out RepairBiome biome)
    {
        biome = default;
        ItemDrop.ItemData? item = recipe?.m_item?.m_itemData;
        if (item != null)
        {
            return TryResolve(item, recipe, out biome);
        }

        lock (SyncRoot)
        {
            return TryResolveRecipeCore(recipe, out biome);
        }
    }

    /// <summary>
    /// Gets a stable prefab identifier for cache/config keys. Inventory clones
    /// prefer their original drop prefab; recipe output data is the fallback.
    /// </summary>
    internal static string GetStablePrefabName(ItemDrop.ItemData? item, Recipe? recipe = null)
    {
        string value = CleanPrefabName(item?.m_dropPrefab?.name);
        if (value.Length > 0)
        {
            return value;
        }

        value = CleanPrefabName(recipe?.m_item?.m_itemData?.m_dropPrefab?.name);
        if (value.Length > 0)
        {
            return value;
        }

        value = CleanPrefabName(recipe?.m_item?.name);
        if (value.Length > 0)
        {
            return value;
        }

        return CleanPrefabName(item?.m_shared?.m_name);
    }

    internal static int GetProgressionRank(RepairBiome biome)
    {
        return biome switch
        {
            RepairBiome.Meadows => 0,
            RepairBiome.BlackForest => 1,
            RepairBiome.Swamp => 2,
            RepairBiome.Ocean => 3,
            RepairBiome.Mountain => 4,
            RepairBiome.Plains => 5,
            RepairBiome.Mistlands => 6,
            RepairBiome.AshLands => 7,
            RepairBiome.DeepNorth => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(biome), biome, "Unknown repair biome.")
        };
    }

    internal static string GetPowderPrefabName(RepairBiome biome)
    {
        _ = GetProgressionRank(biome);
        return PowderPrefabPrefix + biome;
    }

    /// <summary>
    /// Replaces both override maps. Entries use Prefab=Biome and may be
    /// separated by commas, semicolons, or newlines. Later duplicate entries
    /// replace earlier entries. Invalid entries are ignored and returned as
    /// human-readable warnings for the caller to log.
    /// </summary>
    internal static IReadOnlyList<string> ReloadOverrides(string? itemOverrides, string? ingredientOverrides)
    {
        List<string> warnings = new();
        Dictionary<string, RepairBiome> parsedItemOverrides = ParseOverrides(itemOverrides, "item", warnings);
        Dictionary<string, RepairBiome> parsedIngredientOverrides = ParseOverrides(ingredientOverrides, "ingredient", warnings);

        lock (SyncRoot)
        {
            _itemOverrides = parsedItemOverrides;
            _ingredientOverrides = parsedIngredientOverrides;
            ResolutionCache.Clear();
            _recipeCatalogRevision = RepairRecipeCatalog.Revision;
        }

        return warnings;
    }

    internal static void Invalidate()
    {
        lock (SyncRoot)
        {
            ResolutionCache.Clear();
            _recipeCatalogRevision = -1;
        }
    }

    internal static string NormalizeResourceToken(string? token)
    {
        string text = CleanPrefabName(token);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("$item_".Length);
        }
        else if (text.StartsWith("$", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        char[] normalized = new char[text.Length];
        int length = 0;
        foreach (char character in text)
        {
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }

            normalized[length++] = char.ToLowerInvariant(character);
        }

        return length == 0 ? string.Empty : new string(normalized, 0, length);
    }

    private static bool TryResolveRecipeCore(Recipe? recipe, out RepairBiome biome)
    {
        biome = default;
        if (recipe == null)
        {
            return false;
        }

        Piece.Requirement[] requirements = recipe.m_resources ?? Array.Empty<Piece.Requirement>();
        bool found = false;
        int highestRank = -1;

        foreach (Piece.Requirement requirement in requirements)
        {
            if (requirement == null || requirement.m_amount <= 0 || requirement.m_resItem == null)
            {
                continue;
            }

            if (!TryResolveIngredient(requirement.m_resItem, out RepairBiome ingredientBiome))
            {
                continue;
            }

            int rank = GetProgressionRank(ingredientBiome);
            if (!found || rank > highestRank)
            {
                biome = ingredientBiome;
                highestRank = rank;
                found = true;
            }
        }

        return found;
    }

    private static bool TryResolveOutputRecipes(
        string stablePrefabName,
        Recipe? fallbackRecipe,
        out RepairBiome biome)
    {
        biome = default;
        bool foundMappedRecipe = false;
        bool foundEnabledRecipe = false;
        int highestRank = -1;

        IReadOnlyList<Recipe> recipes = RepairRecipeCatalog.GetRecipes(stablePrefabName);
        foreach (Recipe candidate in recipes)
        {
            if (candidate == null || !candidate.m_enabled)
            {
                continue;
            }

            foundEnabledRecipe = true;
            if (!TryResolveRecipeCore(candidate, out RepairBiome candidateBiome))
            {
                continue;
            }

            int rank = GetProgressionRank(candidateBiome);
            if (!foundMappedRecipe || rank > highestRank)
            {
                biome = candidateBiome;
                highestRank = rank;
                foundMappedRecipe = true;
            }
        }

        if (foundEnabledRecipe)
        {
            return foundMappedRecipe;
        }

        // Disabled recipes still describe dropped/shop equipment that Valheim
        // permits repairing even though players cannot craft it directly.
        foreach (Recipe candidate in recipes)
        {
            if (candidate == null || candidate.m_enabled
                || !TryResolveRecipeCore(candidate, out RepairBiome candidateBiome))
            {
                continue;
            }

            int rank = GetProgressionRank(candidateBiome);
            if (!foundMappedRecipe || rank > highestRank)
            {
                biome = candidateBiome;
                highestRank = rank;
                foundMappedRecipe = true;
            }
        }

        if (recipes.Count > 0)
        {
            return foundMappedRecipe;
        }

        if (fallbackRecipe == null || !fallbackRecipe.m_enabled)
        {
            return false;
        }

        string targetToken = NormalizeResourceToken(stablePrefabName);
        string fallbackOutputToken = NormalizeResourceToken(RepairRecipeCatalog.ResolveOutputPrefabName(fallbackRecipe));
        return (targetToken.Length == 0 || string.Equals(fallbackOutputToken, targetToken, StringComparison.Ordinal))
            && TryResolveRecipeCore(fallbackRecipe, out biome);
    }

    private static bool TryResolveIngredient(ItemDrop ingredient, out RepairBiome biome)
    {
        List<string> tokens = GetIngredientTokens(ingredient);

        // Explicit ingredient assignments take precedence regardless of which
        // of the prefab/drop/shared tokens matched the built-in resource map.
        if (TryResolveAnyToken(tokens, _ingredientOverrides, out biome))
        {
            return true;
        }

        return TryResolveAnyToken(tokens, BuiltInIngredientBiomes, out biome);
    }

    private static bool TryResolveAnyToken(
        IEnumerable<string> tokens,
        IReadOnlyDictionary<string, RepairBiome> map,
        out RepairBiome biome)
    {
        foreach (string token in tokens)
        {
            if (map.TryGetValue(token, out biome))
            {
                return true;
            }
        }

        biome = default;
        return false;
    }

    private static List<string> GetOutputTokens(ItemDrop.ItemData item)
    {
        List<string> tokens = new(2);
        AddNormalizedToken(tokens, item.m_dropPrefab?.name);
        AddNormalizedToken(tokens, item.m_shared?.m_name);
        return tokens;
    }

    private static List<string> GetIngredientTokens(ItemDrop ingredient)
    {
        List<string> tokens = new(3);
        AddNormalizedToken(tokens, ingredient.name);
        AddNormalizedToken(tokens, ingredient.m_itemData?.m_dropPrefab?.name);
        AddNormalizedToken(tokens, ingredient.m_itemData?.m_shared?.m_name);
        return tokens;
    }

    private static void AddNormalizedToken(ICollection<string> tokens, string? value)
    {
        string token = NormalizeResourceToken(value);
        if (token.Length > 0 && !tokens.Contains(token))
        {
            tokens.Add(token);
        }
    }

    private static Dictionary<string, RepairBiome> ParseOverrides(
        string? serialized,
        string kind,
        ICollection<string> warnings)
    {
        Dictionary<string, RepairBiome> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return result;
        }

        string[] entries = serialized!.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawEntry in entries)
        {
            string entry = rawEntry.Trim();
            int separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1 || entry.IndexOf('=', separator + 1) >= 0)
            {
                warnings.Add($"Ignored invalid {kind} repair-biome override '{entry}'; expected Prefab=Biome.");
                continue;
            }

            string token = NormalizeResourceToken(entry.Substring(0, separator));
            string biomeText = entry.Substring(separator + 1).Trim();
            if (token.Length == 0)
            {
                warnings.Add($"Ignored invalid {kind} repair-biome override '{entry}'; prefab is empty.");
                continue;
            }

            if (!TryParseBiome(biomeText, out RepairBiome biome))
            {
                warnings.Add($"Ignored invalid {kind} repair-biome override '{entry}'; biome is unknown.");
                continue;
            }

            result[token] = biome;
        }

        return result;
    }

    private static bool TryParseBiome(string? value, out RepairBiome biome)
    {
        string token = NormalizeResourceToken(value);
        foreach (RepairBiome candidate in (RepairBiome[])Enum.GetValues(typeof(RepairBiome)))
        {
            if (string.Equals(token, NormalizeResourceToken(candidate.ToString()), StringComparison.Ordinal))
            {
                biome = candidate;
                return true;
            }
        }

        biome = default;
        return false;
    }

    private static string CleanPrefabName(string? value)
    {
        string result = value?.Trim() ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        return result.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
            ? result.Substring(0, result.Length - cloneSuffix.Length).Trim()
            : result;
    }

    private static Dictionary<string, RepairBiome> BuildBuiltInIngredientMap()
    {
        Dictionary<string, RepairBiome> result = new(StringComparer.OrdinalIgnoreCase);

        AddMaterials(result, RepairBiome.Meadows,
            "Wood", "Stone", "Resin", "Dandelion", "Flint", "LeatherScraps", "BoneFragments", "Honey",
            "Raspberry", "Blueberries", "DeerHide", "DeerMeat", "Feathers", "GreydwarfEye", "RawMeat");

        AddMaterials(result, RepairBiome.BlackForest,
            "HardAntler", "Bronze", "BronzeNails", "Copper", "Tin", "Ectoplasm", "SurtlingCore", "TrollHide",
            "BjornHide", "FineWood", "AncientSeed", "Carrot", "BjornMeat", "BjornPaw", "RoundLog", "Thistle");

        AddMaterials(result, RepairBiome.Swamp,
            "Iron", "Ooze", "Entrails", "Guck", "Bloodbag", "Chain", "ElderBark", "IronNails", "Root", "Turnip",
            "WitheredBone", "CuredSquirrelHamstring");

        AddMaterials(result, RepairBiome.Ocean,
            "Chitin", "Resin", "SerpentScale", "SerpentMeat");

        AddMaterials(result, RepairBiome.Mountain,
            "Silver", "Crystal", "DragonEgg", "JuteRed", "Obsidian", "WolfClaw", "WolfFang", "WolfHairBundle",
            "WolfMeat", "WolfPelt");

        AddMaterials(result, RepairBiome.Plains,
            "UndeadBjornRibcage", "BlackMetal", "DragonTear", "Barley", "BarleyFlour", "BoneFragments", "ChickenEgg",
            "ChickenMeat", "Flax", "GoblinTotem", "LinenThread", "LoxMeat", "LoxPelt", "Needle", "Tar");

        AddMaterials(result, RepairBiome.Mistlands,
            "Eitr", "Bilebag", "BlackCore", "BlackMarble", "BugMeat", "Carapace", "DvergrKeyFragment", "DvergrNeedle",
            "GiantBloodSack", "HareMeat", "JuteBlue", "Mandible", "Sap", "ScaleHide", "Softtissue", "Wisp",
            "YagluthDrop", "YggdrasilWood");

        AddMaterials(result, RepairBiome.AshLands,
            "FlametalNew", "AskBladder", "AskHide", "AsksvinEgg", "AsksvinMeat", "Blackwood", "BoneMawSerpentMeat",
            "BonemawSerpentTooth", "CelestialFeather", "CeramicPlate", "CharcoalResin", "CharredBone", "CharredCogwheel",
            "Charredskull", "Grausten", "MoltenCore", "MorgenHeart", "MorgenSinew", "ProustitePowder", "SulfurStone",
            "VoltureEgg", "VoltureMeat");

        // DeepNorth is retained in the progression enum and powder naming even
        // though VES's current default resource map does not assign materials.
        AddMaterials(result, RepairBiome.DeepNorth);

        return result;
    }

    private static void AddMaterials(
        IDictionary<string, RepairBiome> map,
        RepairBiome biome,
        params string[] materials)
    {
        foreach (string material in materials)
        {
            string token = NormalizeResourceToken(material);
            if (token.Length > 0 && !map.ContainsKey(token))
            {
                // Ordered first mapping wins, matching VES resource-map behavior.
                map[token] = biome;
            }
        }
    }
}
