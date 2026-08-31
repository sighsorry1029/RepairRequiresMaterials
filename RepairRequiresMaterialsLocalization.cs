using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using YamlDotNet.Serialization;

namespace RepairRequiresMaterials;

// Loader flow derived from AzumattDev/LocalizationManager (MIT-0), narrowed
// to this plugin's embedded and user-supplied translations.
internal static class RepairRequiresMaterialsLocalization
{
    private static readonly string[] FileExtensions = { ".json", ".yml" };
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreFields()
        .Build();

    private static Dictionary<string, string> _englishTexts =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal static void Initialize()
    {
        _englishTexts = new Dictionary<string, string>(
            LoadRequiredEmbeddedEnglish(),
            StringComparer.Ordinal);
    }

    internal static void Shutdown()
    {
        _englishTexts = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static void LoadLocalizationLater()
    {
        if (_englishTexts.Count > 0 && Localization.instance != null)
        {
            LoadLocalization(Localization.instance, Localization.instance.GetSelectedLanguage());
        }
    }

    internal static string Localize(string token, params object[] args)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        string localized = GetEnglishFallback(token);
        if (Localization.instance != null)
        {
            try
            {
                string key = GetTokenKey(token);
                string candidate = Localization.instance.Localize(token);
                if (!string.IsNullOrEmpty(candidate)
                    && !string.Equals(candidate, token, StringComparison.Ordinal)
                    && !string.Equals(candidate, "[" + key + "]", StringComparison.Ordinal))
                {
                    localized = candidate;
                }
            }
            catch (Exception exception)
            {
                Warn($"Failed to localize token '{token}'. Using its English fallback.", exception);
            }
        }

        if (args == null || args.Length == 0)
        {
            return localized;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, localized, args);
        }
        catch (Exception exception)
        {
            Warn($"Failed to format localization token '{token}'. Using the unformatted text.", exception);
            return localized;
        }
    }

    internal static void LoadLocalization(Localization localization, string language)
    {
        if (_englishTexts.Count == 0)
        {
            return;
        }

        Dictionary<string, string> localizedTexts =
            new Dictionary<string, string>(_englishTexts, StringComparer.Ordinal);
        string selectedLanguage = string.IsNullOrWhiteSpace(language) ? "English" : language;

        if (!selectedLanguage.Equals("English", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, string>? embeddedOverlay =
                TryLoadOptionalEmbeddedTranslation(selectedLanguage);
            if (embeddedOverlay != null)
            {
                Overlay(localizedTexts, embeddedOverlay);
            }
        }

        // Files shipped beside plugins extend the embedded language, while a
        // config-side file is the final user override.
        OverlayExternalTranslation(localizedTexts, Paths.PluginPath, selectedLanguage);
        OverlayExternalTranslation(localizedTexts, Paths.ConfigPath, selectedLanguage);

        foreach (KeyValuePair<string, string> entry in localizedTexts)
        {
            localization.AddWord(entry.Key, entry.Value);
        }

        // AddWord does not invalidate Valheim's localization result cache. This
        // also makes the late SetupGui fallback replace a token that another mod
        // may have asked Localization to resolve before our words were registered.
        localization.m_cache.EvictAll();
    }

    private static Dictionary<string, string> LoadRequiredEmbeddedEnglish()
    {
        EmbeddedTranslationResult result = TryReadEmbeddedTranslation(
            "English",
            out Dictionary<string, string>? translations,
            out Exception? error);

        if (result == EmbeddedTranslationResult.Loaded && translations != null && translations.Count > 0)
        {
            return translations;
        }

        string reason = result == EmbeddedTranslationResult.NotFound
            ? "the resource was not found"
            : error?.Message ?? "the resource was empty";
        throw new InvalidOperationException(
            $"RepairRequiresMaterials requires an embedded translations/English.yml or " +
            $"translations/English.json file, but {reason}.",
            error);
    }

    private static Dictionary<string, string>? TryLoadOptionalEmbeddedTranslation(string language)
    {
        EmbeddedTranslationResult result = TryReadEmbeddedTranslation(
            language,
            out Dictionary<string, string>? translations,
            out Exception? error);

        if (result == EmbeddedTranslationResult.Failed)
        {
            Warn(
                $"Failed to load embedded {language} localization. Keeping the embedded English text.",
                error);
            return null;
        }

        return result == EmbeddedTranslationResult.Loaded ? translations : null;
    }

    private static EmbeddedTranslationResult TryReadEmbeddedTranslation(
        string language,
        out Dictionary<string, string>? translations,
        out Exception? error)
    {
        translations = null;
        error = null;
        Assembly assembly = typeof(RepairRequiresMaterialsLocalization).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();

        foreach (string extension in FileExtensions)
        {
            string suffix = "translations." + language + extension;
            string? resourceName = resourceNames.FirstOrDefault(
                name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                continue;
            }

            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    throw new IOException($"Could not open embedded resource '{resourceName}'.");
                }

                using StreamReader reader = new StreamReader(stream, Encoding.UTF8, true);
                translations = Deserialize(reader.ReadToEnd());
                return EmbeddedTranslationResult.Loaded;
            }
            catch (Exception exception)
            {
                error = exception;
                return EmbeddedTranslationResult.Failed;
            }
        }

        return EmbeddedTranslationResult.NotFound;
    }

    private static void OverlayExternalTranslation(
        IDictionary<string, string> destination,
        string root,
        string language)
    {
        Dictionary<string, string>? translations = TryLoadExternalTranslationFromRoot(
            root,
            language);
        if (translations != null)
        {
            Overlay(destination, translations);
        }
    }

    private static Dictionary<string, string>? TryLoadExternalTranslationFromRoot(
        string root,
        string language)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return null;
        }

        List<string> candidates;
        try
        {
            string expectedName = $"{RepairRequiresMaterialsPlugin.ModName}.{language}";
            candidates = Directory
                .EnumerateFiles(root, $"{RepairRequiresMaterialsPlugin.ModName}.*", SearchOption.AllDirectories)
                .Where(path => FileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => Path.GetFileNameWithoutExtension(path)
                    .Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetExtensionPriority)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            Warn(
                $"Failed to search '{root}' for the {language} localization. " +
                "Keeping the localization layers loaded so far.",
                exception);
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        string selectedFile = candidates[0];
        if (candidates.Count > 1)
        {
            Warn(
                $"Multiple {language} localization files were found under '{root}'. " +
                $"Using '{selectedFile}'.");
        }

        try
        {
            return Deserialize(File.ReadAllText(selectedFile, Encoding.UTF8));
        }
        catch (Exception exception)
        {
            Warn(
                $"Failed to read or parse external localization '{selectedFile}'. " +
                "Keeping the localization layers loaded so far.",
                exception);
            return null;
        }
    }

    private static Dictionary<string, string> Deserialize(string data) =>
        Deserializer.Deserialize<Dictionary<string, string>?>(data) ??
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static void Overlay(
        IDictionary<string, string> destination,
        IEnumerable<KeyValuePair<string, string>> overlay)
    {
        foreach (KeyValuePair<string, string> entry in overlay)
        {
            if (string.IsNullOrEmpty(entry.Key) || entry.Value == null)
            {
                continue;
            }

            destination[entry.Key] = entry.Value;
        }
    }

    private static int GetExtensionPriority(string path)
    {
        string extension = Path.GetExtension(path);
        for (int index = 0; index < FileExtensions.Length; ++index)
        {
            if (extension.Equals(FileExtensions[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return FileExtensions.Length;
    }

    private static string GetEnglishFallback(string token)
    {
        string key = GetTokenKey(token);
        return _englishTexts.TryGetValue(key, out string? text) ? text : token;
    }

    private static string GetTokenKey(string token) =>
        token.Length > 0 && token[0] == '$' ? token.Substring(1) : token;

    private static void Warn(string message, Exception? exception = null)
    {
        string text = exception == null ? message : $"{message} {exception.Message}";
        RepairRequiresMaterialsPlugin.Log.LogWarning(text);
    }

    private enum EmbeddedTranslationResult
    {
        NotFound,
        Loaded,
        Failed
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class RepairRequiresMaterialsLocalizationLanguagePatch
{
    [HarmonyPostfix]
    private static void Postfix(Localization __instance, string language) =>
        RepairRequiresMaterialsLocalization.LoadLocalization(__instance, language);
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.SetupGui))]
internal static class RepairRequiresMaterialsLocalizationGuiPatch
{
    [HarmonyPostfix]
    private static void Postfix() => RepairRequiresMaterialsLocalization.LoadLocalizationLater();
}
