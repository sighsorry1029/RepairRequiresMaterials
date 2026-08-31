using System;
using System.Collections.Generic;

namespace RepairRequiresMaterials;

internal sealed class PrefabPatternMatcher
{
    internal static readonly PrefabPatternMatcher Empty = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    private static readonly char[] Separators = { ',', ';', '\r', '\n' };

    private readonly HashSet<string> _exactNames;
    private readonly string[] _wildcardPatterns;

    private PrefabPatternMatcher(
        HashSet<string> exactNames,
        string[] wildcardPatterns)
    {
        _exactNames = exactNames;
        _wildcardPatterns = wildcardPatterns;
    }

    internal static PrefabPatternMatcher Parse(string? source)
    {
        if (source == null || string.IsNullOrWhiteSpace(source))
        {
            return Empty;
        }

        HashSet<string> exactNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> uniqueWildcards = new(StringComparer.OrdinalIgnoreCase);
        List<string> wildcardPatterns = new();

        foreach (string entry in source.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string pattern = entry.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.IndexOf('*') < 0)
            {
                exactNames.Add(pattern);
            }
            else if (uniqueWildcards.Add(pattern))
            {
                wildcardPatterns.Add(pattern);
            }
        }

        return exactNames.Count == 0 && wildcardPatterns.Count == 0
            ? Empty
            : new PrefabPatternMatcher(exactNames, wildcardPatterns.ToArray());
    }

    internal bool IsMatch(string? prefabName)
    {
        if (prefabName == null || prefabName.Length == 0)
        {
            return false;
        }

        if (_exactNames.Contains(prefabName))
        {
            return true;
        }

        foreach (string pattern in _wildcardPatterns)
        {
            if (WildcardMatches(prefabName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatches(string value, string pattern)
    {
        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int retryValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && pattern[patternIndex] != '*'
                && CharactersEqual(value[valueIndex], pattern[patternIndex]))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
                continue;
            }

            if (starIndex < 0)
            {
                return false;
            }

            patternIndex = starIndex + 1;
            valueIndex = ++retryValueIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool CharactersEqual(char left, char right)
    {
        return left == right || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }
}
