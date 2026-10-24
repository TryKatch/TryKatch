using Microsoft.Extensions.Options;

namespace Trykatch.Infrastructure.Caching;

internal enum OrganizationCacheMode
{
    Disabled,
    LocalOnly,
    Distributed
}

internal sealed class OrganizationCacheOptions
{
    public const string SectionName = "Cache";
    public const int MaximumPayloadBytes = 65_536;

    public OrganizationCacheMode Mode { get; init; } = OrganizationCacheMode.Disabled;
    public string? ApplicationNamespace { get; init; }
    public Dictionary<string, int> TtlSeconds { get; init; } = new(StringComparer.Ordinal)
    {
        ["short"] = 30,
        ["standard"] = 120
    };
    public TimeSpan FactoryTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed class OrganizationCacheOptionsValidator : IValidateOptions<OrganizationCacheOptions>
{
    private const string InvalidMode = "Cache mode must be a defined value.";
    private const string InvalidApplicationNamespace = "Cache application namespace must be a lower-case kebab identifier of at most 120 characters.";
    private const string TooManyTtlPolicies = "Cache TTL policies may contain at most 32 entries.";
    private const string MissingRequiredTtlPolicies = "Cache TTL policies must include short and standard.";
    private const string InvalidTtlPolicyKey = "Cache TTL policy keys must be lower-case kebab identifiers of at most 64 characters.";
    private const string InvalidTtlDuration = "Cache TTL policy durations must be between 1 and 600 seconds.";
    private const string InvalidFactoryTimeout = "Cache factory timeout must be between 1 and 30 seconds.";

    public ValidateOptionsResult Validate(string? name, OrganizationCacheOptions options)
    {
        List<string> failures = [];

        if (!Enum.IsDefined(options.Mode))
            failures.Add(InvalidMode);

        if (options.ApplicationNamespace is not null && !IsLowerKebab(options.ApplicationNamespace, 120))
            failures.Add(InvalidApplicationNamespace);

        Dictionary<string, int>? ttlSeconds = options.TtlSeconds;
        if (ttlSeconds is null)
        {
            failures.Add(MissingRequiredTtlPolicies);
        }
        else
        {
            if (ttlSeconds.Count > 32)
                failures.Add(TooManyTtlPolicies);
            if (!ttlSeconds.ContainsKey("short") || !ttlSeconds.ContainsKey("standard"))
                failures.Add(MissingRequiredTtlPolicies);

            foreach ((string key, int seconds) in ttlSeconds)
            {
                if (!IsLowerKebab(key, 64))
                    failures.Add(InvalidTtlPolicyKey);
                if (seconds is < 1 or > 600)
                    failures.Add(InvalidTtlDuration);
            }
        }

        if (options.FactoryTimeout < TimeSpan.FromSeconds(1)
            || options.FactoryTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add(InvalidFactoryTimeout);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsLowerKebab(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
            return false;

        bool previousWasSeparator = true;
        foreach (char character in value)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                previousWasSeparator = false;
                continue;
            }

            if (character != '-' || previousWasSeparator)
                return false;
            previousWasSeparator = true;
        }

        return !previousWasSeparator;
    }
}
