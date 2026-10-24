using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Features;

namespace Trykatch.Infrastructure.Features;

internal static class FeaturePolicyOptions
{
    private const int MaximumEntries = 10_000;
    private const int MaximumFeatureKeyLength = 120;

    internal static FeaturePolicySettings CreateSettings(IConfiguration configuration, string environmentName)
    {
        if (configuration is null)
            throw InvalidConfiguration();

        string normalizedEnvironment = environmentName?.ToLowerInvariant() ?? string.Empty;
        if (!IsEnvironmentName(normalizedEnvironment))
            throw InvalidEnvironment();

        IConfigurationSection section = configuration.GetSection("Features");
        if (HasScalarValue(section))
            throw InvalidConfiguration();

        HashSet<string> disabled = new(StringComparer.Ordinal);
        Dictionary<string, bool> defaults = new(StringComparer.Ordinal);

        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (child.Key.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                ReadDisabled(child, disabled);
            else if (child.Key.Equals("Defaults", StringComparison.OrdinalIgnoreCase))
                ReadDefaults(child, defaults);
            else
                throw InvalidConfiguration();
        }

        return new FeaturePolicySettings(
            normalizedEnvironment,
            disabled.ToFrozenSet(StringComparer.Ordinal),
            defaults.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void ReadDisabled(IConfigurationSection section, HashSet<string> disabled)
    {
        if (HasScalarValue(section))
            throw InvalidConfiguration();

        IConfigurationSection[] entries = section.GetChildren().ToArray();
        if (entries.Length > MaximumEntries)
            throw InvalidConfiguration();

        (int Index, IConfigurationSection Section)[] indexedEntries = new (int, IConfigurationSection)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            string key = entries[i].Key;
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 0
                || !index.ToString(CultureInfo.InvariantCulture).Equals(key, StringComparison.Ordinal))
            {
                throw InvalidConfiguration();
            }

            indexedEntries[i] = (index, entries[i]);
        }

        Array.Sort(indexedEntries, static (left, right) => left.Index.CompareTo(right.Index));
        for (int i = 0; i < indexedEntries.Length; i++)
        {
            IConfigurationSection entry = indexedEntries[i].Section;
            if (indexedEntries[i].Index != i || entry.Value is null || entry.GetChildren().Any()
                || !IsFeatureKey(entry.Value) || !disabled.Add(entry.Value))
            {
                throw InvalidConfiguration();
            }
        }
    }

    private static void ReadDefaults(IConfigurationSection section, Dictionary<string, bool> defaults)
    {
        if (HasScalarValue(section))
            throw InvalidConfiguration();

        IConfigurationSection[] entries = section.GetChildren().ToArray();
        if (entries.Length > MaximumEntries)
            throw InvalidConfiguration();

        foreach (IConfigurationSection entry in entries)
        {
            if (!IsFeatureKey(entry.Key) || entry.Value is null || entry.GetChildren().Any()
                || !bool.TryParse(entry.Value, out bool enabled)
                || !defaults.TryAdd(entry.Key, enabled))
            {
                throw InvalidConfiguration();
            }
        }
    }

    private static bool IsEnvironmentName(string value)
    {
        if (value.Length is < 1 or > 64 || !IsAsciiAlphaNumeric(value[0]))
            return false;

        for (int i = 1; i < value.Length; i++)
        {
            char character = value[i];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
                return false;
        }

        return true;
    }

    // Hosting providers may retain an empty value on a parent that also has children.
    // Treat only that structural marker as an object; leaf values and nonempty scalars remain invalid.
    private static bool HasScalarValue(IConfigurationSection section) =>
        section.Value is not null && (section.Value.Length != 0 || !section.GetChildren().Any());

    private static bool IsFeatureKey(string value)
    {
        if (value.Length is < 1 or > MaximumFeatureKeyLength || !IsAsciiLowerAlpha(value[0]))
            return false;

        bool hasDot = false;
        bool previousWasSeparator = false;
        for (int i = 1; i < value.Length; i++)
        {
            char character = value[i];
            if (IsAsciiLowerAlpha(character) || IsAsciiDigit(character))
            {
                previousWasSeparator = false;
                continue;
            }

            if ((character is '.' or '-') && !previousWasSeparator && i < value.Length - 1)
            {
                hasDot |= character == '.';
                previousWasSeparator = true;
                continue;
            }

            return false;
        }

        return hasDot && !previousWasSeparator;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        IsAsciiLowerAlpha(value) || value is >= 'A' and <= 'Z' || IsAsciiDigit(value);

    private static bool IsAsciiLowerAlpha(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static InvalidOperationException InvalidEnvironment() =>
        new("Feature policy environment name is invalid.");

    private static InvalidOperationException InvalidConfiguration() =>
        new("Feature policy configuration is invalid.");
}
