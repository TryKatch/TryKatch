using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Trykatch.Infrastructure.Jobs;

internal sealed record ModuleRecurringTarget(
    string ScheduleKey,
    string TargetKey,
    Guid OrganizationId,
    JsonElement Payload,
    DateTimeOffset StartAtUtc,
    int? MaxCatchUpOccurrences,
    TimeSpan? MaxCatchUpAge);

/// <summary>Explicit host-owned bindings between reviewed recurring schedules and organizations.</summary>
public static class ModuleRecurringTargetRegistration
{
    public static IServiceCollection AddModuleRecurringTarget(
        this IServiceCollection services,
        string scheduleKey,
        string targetKey,
        Guid organizationId,
        JsonElement payload,
        DateTimeOffset startAtUtc,
        int? maxCatchUpOccurrences = null,
        TimeSpan? maxCatchUpAge = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!IsStableKey(scheduleKey))
            throw new ArgumentException("Schedule keys must be stable lower-case identifiers of at most 120 characters.", nameof(scheduleKey));
        if (!IsStableKey(targetKey))
            throw new ArgumentException("Target keys must be stable lower-case identifiers of at most 120 characters.", nameof(targetKey));
        if (organizationId == Guid.Empty)
            throw new ArgumentException("A target organization is required.", nameof(organizationId));
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || Encoding.UTF8.GetByteCount(payload.GetRawText()) > 1_048_576)
            throw new ArgumentException("A non-null target payload no larger than 1 MiB is required.", nameof(payload));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startAtUtc, DateTimeOffset.MinValue.AddTicks(10));
        if (startAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The target start time must be expressed in UTC.", nameof(startAtUtc));
        if ((maxCatchUpOccurrences is null) != (maxCatchUpAge is null))
            throw new ArgumentException("Catch-up occurrence and age bounds must be supplied together.");
        if (maxCatchUpOccurrences is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCatchUpOccurrences), "Catch-up is limited to 1 through 100 occurrences.");
        if (maxCatchUpAge is { } age && (age <= TimeSpan.Zero || age > TimeSpan.FromDays(31)))
            throw new ArgumentOutOfRangeException(nameof(maxCatchUpAge), "Catch-up age must be positive and no greater than 31 days.");

        ModuleRecurringTarget target = new(
            scheduleKey,
            targetKey,
            organizationId,
            payload.Clone(),
            ModuleJobIntent.NormalizeDue(startAtUtc),
            maxCatchUpOccurrences,
            maxCatchUpAge);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleRecurringTarget)
                && descriptor.ImplementationInstance is ModuleRecurringTarget registered
                && registered.ScheduleKey == scheduleKey && registered.TargetKey == targetKey))
            throw new InvalidOperationException($"Recurring target '{scheduleKey}/{targetKey}' is already registered.");
        services.AddSingleton(target);
        return services;
    }

    private static bool IsStableKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 120 || value[0] is < 'a' or > 'z') return false;
        bool separator = false;
        foreach (char character in value)
        {
            bool valid = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.';
            if (!valid || (character is '-' or '.') && separator) return false;
            separator = character is '-' or '.';
        }
        return !separator;
    }
}
