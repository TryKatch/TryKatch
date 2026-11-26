using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaOptions
{
    private const int MaximumEntries = 10_000;
    private const string HashSchema = "trykatch.organization-quota-policy";

    internal static QuotaSettings CreateSettings(IConfiguration configuration, ModuleCatalog catalog)
    {
        if (configuration is null || catalog is null)
            throw InvalidConfiguration();

        IConfigurationSection section = configuration.GetSection("Quotas");
        if (HasScalarValue(section))
            throw InvalidConfiguration();

        bool enforcementEnabled = false;
        long revision = 0;
        bool enforcementConfigured = false;
        bool revisionConfigured = false;
        IConfigurationSection? resourcesSection = null;

        IConfigurationSection[] properties = section.GetChildren().ToArray();
        if (properties.Length > 3)
            throw InvalidConfiguration();

        foreach (IConfigurationSection property in properties)
        {
            switch (property.Key)
            {
                case "EnforcementEnabled":
                    if (enforcementConfigured || HasChildren(property) || property.Value is null
                        || !bool.TryParse(property.Value, out enforcementEnabled))
                        throw InvalidConfiguration();
                    enforcementConfigured = true;
                    break;
                case "Revision":
                    if (revisionConfigured || HasChildren(property) || property.Value is null
                        || !long.TryParse(property.Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
                        || revision < 0)
                        throw InvalidConfiguration();
                    revisionConfigured = true;
                    break;
                case "Resources":
                    if (resourcesSection is not null || HasScalarValue(property))
                        throw InvalidConfiguration();
                    resourcesSection = property;
                    break;
                default:
                    throw InvalidConfiguration();
            }
        }

        HashSet<string> declaredResourceKeys = catalog.Descriptors
            .SelectMany(descriptor => descriptor.QuotaResources)
            .Select(resource => resource.Key)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, QuotaAllowance> allowances = new(StringComparer.Ordinal);

        if (resourcesSection is not null)
        {
            IConfigurationSection[] entries = resourcesSection.GetChildren().ToArray();
            if (entries.Length > MaximumEntries)
                throw InvalidConfiguration();

            foreach (IConfigurationSection entry in entries)
            {
                if (!declaredResourceKeys.Contains(entry.Key) || HasScalarValue(entry))
                    throw InvalidConfiguration();

                QuotaAllowance allowance = ReadAllowance(entry);
                if (!allowances.TryAdd(entry.Key, allowance))
                    throw InvalidConfiguration();
            }
        }

        if (revision == 0 && (enforcementEnabled || allowances.Count != 0))
            throw InvalidConfiguration();

        string hash = ComputeHash(enforcementEnabled, allowances);
        return new QuotaSettings(
            enforcementEnabled,
            revision,
            hash,
            allowances.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static QuotaAllowance ReadAllowance(IConfigurationSection section)
    {
        IConfigurationSection[] properties = section.GetChildren().ToArray();
        if (properties.Length is < 1 or > 2)
            throw InvalidConfiguration();

        long? limit = null;
        bool limitConfigured = false;
        bool unlimited = false;
        bool unlimitedConfigured = false;

        foreach (IConfigurationSection property in properties)
        {
            if (HasChildren(property) || property.Value is null)
                throw InvalidConfiguration();

            switch (property.Key)
            {
                case "Limit":
                    if (limitConfigured
                        || !long.TryParse(property.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedLimit)
                        || parsedLimit < 0)
                        throw InvalidConfiguration();
                    limit = parsedLimit;
                    limitConfigured = true;
                    break;
                case "Unlimited":
                    if (unlimitedConfigured || !bool.TryParse(property.Value, out unlimited))
                        throw InvalidConfiguration();
                    unlimitedConfigured = true;
                    break;
                default:
                    throw InvalidConfiguration();
            }
        }

        if (limitConfigured && unlimited)
            throw InvalidConfiguration();
        if (limitConfigured)
            return new QuotaAllowance(OrganizationEntitlementMode.Finite, limit);
        if (unlimitedConfigured && unlimited)
            return new QuotaAllowance(OrganizationEntitlementMode.Unlimited, null);

        throw InvalidConfiguration();
    }

    private static string ComputeHash(bool enforcementEnabled, IReadOnlyDictionary<string, QuotaAllowance> allowances)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema = HashSchema,
            version = 1,
            enforcementEnabled,
            allowances = allowances
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new
                {
                    resource = entry.Key,
                    mode = entry.Value.Mode switch
                    {
                        OrganizationEntitlementMode.Finite => "finite",
                        OrganizationEntitlementMode.Unlimited => "unlimited",
                        _ => throw InvalidConfiguration()
                    },
                    limit = entry.Value.Limit
                })
        });
        return ModuleCanonicalJson.ComputeHash(json);
    }

    private static bool HasChildren(IConfigurationSection section) => section.GetChildren().Any();

    // Hosting providers may keep an empty marker on an object that also has children.
    private static bool HasScalarValue(IConfigurationSection section) =>
        section.Value is not null && (section.Value.Length != 0 || !HasChildren(section));

    private static InvalidOperationException InvalidConfiguration() =>
        new("Organization quota configuration is invalid.");
}

internal sealed record QuotaSettings(
    bool EnforcementEnabled,
    long Revision,
    string Hash,
    FrozenDictionary<string, QuotaAllowance> Allowances);

internal sealed record QuotaAllowance(OrganizationEntitlementMode Mode, long? Limit);
