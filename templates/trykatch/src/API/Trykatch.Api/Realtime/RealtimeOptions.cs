using Microsoft.Extensions.Options;

namespace Trykatch.Api.Realtime;

public enum RealtimeMode { Disabled, LocalOnly, Distributed }

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";
    public const int MaximumNotificationBytes = 2048;
    public const int MaximumIncomingBytes = 4096;
    public RealtimeMode Mode { get; init; } = RealtimeMode.Disabled;
    public string? ApplicationNamespace { get; init; }
    public int MaximumConnections { get; init; } = 256;
    public int MaximumSubscriptions { get; init; } = 16;
    public int MaximumConcurrentSends { get; init; } = 16;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan AuthorityTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class RealtimeOptionsValidator : IValidateOptions<RealtimeOptions>
{
    public ValidateOptionsResult Validate(string? name, RealtimeOptions options)
    {
        List<string> failures = [];
        if (!Enum.IsDefined(options.Mode)) failures.Add("Realtime mode is invalid.");
        if ((options.Mode == RealtimeMode.Distributed && options.ApplicationNamespace is null)
            || (options.ApplicationNamespace is { } value && !ValidNamespace(value)))
            failures.Add("Realtime application namespace must use 1–120 lowercase letters, digits and single separating hyphens.");
        if (options.MaximumConnections is < 1 or > 256) failures.Add("Realtime connections must be between 1 and 256.");
        if (options.MaximumSubscriptions is < 1 or > 16) failures.Add("Realtime subscriptions must be between 1 and 16.");
        if (options.MaximumConcurrentSends is < 1 or > 16) failures.Add("Realtime concurrent sends must be between 1 and 16.");
        if (!Bounded(options.PollInterval, 30)) failures.Add("Realtime poll interval must be between 100 milliseconds and 30 seconds.");
        if (!Bounded(options.SendTimeout, 10)) failures.Add("Realtime send timeout must be between 100 milliseconds and 10 seconds.");
        if (!Bounded(options.AuthorityTimeout, 30)) failures.Add("Realtime authority timeout must be between 100 milliseconds and 30 seconds.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool Bounded(TimeSpan value, int maximumSeconds) =>
        value >= TimeSpan.FromMilliseconds(100) && value <= TimeSpan.FromSeconds(maximumSeconds);

    private static bool ValidNamespace(string value) => value.Length is >= 1 and <= 120
        && value[0] != '-' && value[^1] != '-' && !value.Contains("--", StringComparison.Ordinal)
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
