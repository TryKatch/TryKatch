using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>Shared, fail-closed validation for installed background-work declarations.</summary>
public static class ModuleWorkRegistrationRules
{
    public const int HostMaximumPayloadBytes = 1024 * 1024;

    private static readonly Regex StableKey = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Validates only explicit installed declarations; this method activates no module code.</summary>
    public static void Validate(IReadOnlyCollection<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        ModuleDescriptor[] installed = modules.ToArray();
        Dictionary<string, PermissionDescriptor> permissions = installed
            .SelectMany(module => module.Permissions.Select(permission => (module.Id, Permission: permission)))
            .ToDictionary(item => item.Permission.Key, item => item.Permission, StringComparer.Ordinal);
        Dictionary<string, ModuleOperationDescriptor> operations = [];
        Dictionary<string, ModuleSystemGrantDescriptor> grants = [];
        Dictionary<(string Key, int Version), IntegrationEventDescriptor> events = [];
        Dictionary<(string Key, int Version), ModuleJobDescriptor> jobs = [];
        List<(ModuleDescriptor Module, ModuleEventConsumerDescriptor Consumer)> consumers = [];
        List<(ModuleDescriptor Module, ModuleRecurringScheduleDescriptor Schedule)> schedules = [];
        HashSet<string> scheduleKeys = new(StringComparer.Ordinal);

        foreach (ModuleDescriptor module in installed)
        {
            bool declaresWork = module.Operations.Count > 0
                || module.SystemGrants.Count > 0
                || module.IntegrationEvents.Count > 0
                || module.EventConsumers.Count > 0
                || module.Jobs.Count > 0
                || module.RecurringSchedules.Count > 0;
            if (declaresWork && !module.Capabilities.HasFlag(ModuleCapabilities.BackgroundWork))
                throw Invalid(module.Id, "declares background work without the BackgroundWork capability");

            foreach (ModuleOperationDescriptor operation in module.Operations)
            {
                RequireOwner(module.Id, operation.ModuleId, $"operation '{operation.OperationKey}'");
                RequireKey(operation.OperationKey, module.Id, "operation");
                RequireVersion(operation.OperationVersion, module.Id, $"operation '{operation.OperationKey}'");
                RequireKind(operation.ExecutionKind, module.Id, $"operation '{operation.OperationKey}'");
                RequireKey(operation.PayloadSchemaKey, module.Id, $"payload schema for '{operation.OperationKey}'");
                RequireVersion(operation.PayloadSchemaVersion, module.Id, $"payload schema for '{operation.OperationKey}'");
                RequirePayloadLimit(operation.MaximumPayloadBytes, module.Id, $"operation '{operation.OperationKey}'");
                RequireKey(operation.ResourcePolicyKey, module.Id, $"resource policy for '{operation.OperationKey}'");
                RequireUnique(operation.RequiredPermissionKeys, module.Id, $"required permission for '{operation.OperationKey}'");
                foreach (string permission in operation.RequiredPermissionKeys)
                {
                    RequireKey(permission, module.Id, $"required permission for '{operation.OperationKey}'");
                    if (!permissions.ContainsKey(permission))
                        throw Invalid(module.Id, $"operation '{operation.OperationKey}' requires unregistered permission '{permission}'");
                }

                if (operation.ExecutionKind == ModuleExecutionKind.UserCommand && operation.SystemGrantKey is not null)
                    throw Invalid(module.Id, $"user operation '{operation.OperationKey}' cannot declare a system grant");
                if (operation.ExecutionKind != ModuleExecutionKind.UserCommand)
                {
                    RequireKey(operation.SystemGrantKey, module.Id, $"system grant for '{operation.OperationKey}'");
                    if (!grants.TryGetValue(operation.SystemGrantKey!, out ModuleSystemGrantDescriptor? grant)
                        && !module.SystemGrants.Any(candidate => candidate.GrantKey == operation.SystemGrantKey))
                        throw Invalid(module.Id, $"operation '{operation.OperationKey}' references an unregistered system grant '{operation.SystemGrantKey}'");
                }

                if (!operations.TryAdd(operation.OperationKey, operation))
                    throw Invalid(module.Id, $"duplicate installed operation key '{operation.OperationKey}'");
            }

            foreach (ModuleSystemGrantDescriptor grant in module.SystemGrants)
            {
                RequireOwner(module.Id, grant.OwnerModuleId, $"system grant '{grant.GrantKey}'");
                RequireKey(grant.GrantKey, module.Id, "system grant");
                RequireVersion(grant.GrantVersion, module.Id, $"system grant '{grant.GrantKey}'");
                if (grant.AuditActorId == Guid.Empty)
                    throw Invalid(module.Id, $"system grant '{grant.GrantKey}' requires a nonempty audit actor id");
                RequireFiniteUniqueKeys(grant.AllowedOperationKeys, module.Id, $"operation set for grant '{grant.GrantKey}'");
                RequireFiniteUniqueKeys(grant.AllowedResourceKeys, module.Id, $"resource set for grant '{grant.GrantKey}'");
                foreach (string resourceKey in grant.AllowedResourceKeys)
                {
                    if (!module.DataResources.Any(resource => resource.Name == resourceKey))
                        throw Invalid(module.Id, $"system grant '{grant.GrantKey}' references undeclared resource '{resourceKey}'");
                }
                if (!grants.TryAdd(grant.GrantKey, grant))
                    throw Invalid(module.Id, $"duplicate installed system grant key '{grant.GrantKey}'");
            }

            foreach (IntegrationEventDescriptor integrationEvent in module.IntegrationEvents)
            {
                RequireOwner(module.Id, integrationEvent.OwnerModuleId, $"event '{integrationEvent.EventKey}'");
                RequireKey(integrationEvent.EventKey, module.Id, "event");
                RequireVersion(integrationEvent.SchemaVersion, module.Id, $"event '{integrationEvent.EventKey}'");
                RequirePayloadLimit(integrationEvent.MaximumPayloadBytes, module.Id, $"event '{integrationEvent.EventKey}'");
                ArgumentNullException.ThrowIfNull(integrationEvent.PayloadType);
                ArgumentNullException.ThrowIfNull(integrationEvent.ReadPayload);
                Type payloadType = integrationEvent.PayloadType;
                if (payloadType.IsAbstract || payloadType.IsInterface || payloadType.ContainsGenericParameters
                    || payloadType.IsPointer || payloadType.IsByRef || payloadType.IsByRefLike)
                    throw Invalid(module.Id, $"event '{integrationEvent.EventKey}' requires an explicit closed JSON-compatible payload type");
                if (!events.TryAdd((integrationEvent.EventKey, integrationEvent.SchemaVersion), integrationEvent))
                    throw Invalid(module.Id, $"duplicate installed event schema '{integrationEvent.EventKey}' version {integrationEvent.SchemaVersion}");
            }

            foreach (ModuleEventConsumerDescriptor consumer in module.EventConsumers)
                consumers.Add((module, consumer));

            foreach (ModuleJobDescriptor job in module.Jobs)
            {
                RequireOwner(module.Id, job.ModuleId, $"job '{job.JobKey}'");
                RequireKey(job.JobKey, module.Id, "job");
                RequireVersion(job.JobVersion, module.Id, $"job '{job.JobKey}'");
                RequireKey(job.OperationKey, module.Id, $"operation for job '{job.JobKey}'");
                RequireVersion(job.OperationVersion, module.Id, $"operation for job '{job.JobKey}'");
                RequireKind(job.ExecutionKind, module.Id, $"job '{job.JobKey}'");
                RequireKey(job.PayloadSchemaKey, module.Id, $"payload schema for job '{job.JobKey}'");
                RequireVersion(job.PayloadSchemaVersion, module.Id, $"payload schema for job '{job.JobKey}'");
                RequirePayloadLimit(job.MaximumPayloadBytes, module.Id, $"job '{job.JobKey}'");
                if (!jobs.TryAdd((job.JobKey, job.JobVersion), job))
                    throw Invalid(module.Id, $"duplicate installed job '{job.JobKey}' version {job.JobVersion}");
            }

            foreach (ModuleRecurringScheduleDescriptor schedule in module.RecurringSchedules)
                schedules.Add((module, schedule));
        }

        foreach (ModuleSystemGrantDescriptor grant in grants.Values)
        {
            foreach (string operationKey in grant.AllowedOperationKeys)
            {
                if (!operations.TryGetValue(operationKey, out ModuleOperationDescriptor? operation)
                    || operation.ModuleId != grant.OwnerModuleId
                    || operation.SystemGrantKey != grant.GrantKey)
                    throw Invalid(grant.OwnerModuleId, $"system grant '{grant.GrantKey}' references an operation it does not own: '{operationKey}'");
                if (!grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal))
                    throw Invalid(grant.OwnerModuleId, $"system grant '{grant.GrantKey}' does not allow operation '{operationKey}' resource policy '{operation.ResourcePolicyKey}'");
            }
        }

        foreach (ModuleOperationDescriptor operation in operations.Values)
        {
            if (operation.ExecutionKind == ModuleExecutionKind.UserCommand)
                continue;
            if (!grants.TryGetValue(operation.SystemGrantKey!, out ModuleSystemGrantDescriptor? grant)
                || grant.OwnerModuleId != operation.ModuleId
                || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
                || !grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal))
                throw Invalid(operation.ModuleId, $"operation '{operation.OperationKey}' has no matching finite system grant for its operation and resource");
        }

        foreach (ModuleJobDescriptor job in jobs.Values)
        {
            if (!operations.TryGetValue(job.OperationKey, out ModuleOperationDescriptor? operation)
                || operation.ModuleId != job.ModuleId
                || operation.OperationVersion != job.OperationVersion
                || operation.ExecutionKind != job.ExecutionKind
                || operation.PayloadSchemaKey != job.PayloadSchemaKey
                || operation.PayloadSchemaVersion != job.PayloadSchemaVersion
                || job.MaximumPayloadBytes > operation.MaximumPayloadBytes)
                throw Invalid(job.ModuleId, $"job '{job.JobKey}' does not resolve to a compatible owned operation and payload schema");
        }

        HashSet<string> consumerKeys = new(StringComparer.Ordinal);
        foreach ((ModuleDescriptor module, ModuleEventConsumerDescriptor consumer) in consumers)
        {
            RequireOwner(module.Id, consumer.OwnerModuleId, $"event consumer '{consumer.ConsumerKey}'");
            RequireKey(consumer.ConsumerKey, module.Id, "event consumer");
            RequireUnique(consumer.AcceptedEvents.Select(item => $"{item.EventKey}/{item.SchemaVersion}"), module.Id, $"accepted event for '{consumer.ConsumerKey}'");
            if (consumer.AcceptedEvents.Count == 0)
                throw Invalid(module.Id, $"event consumer '{consumer.ConsumerKey}' must accept at least one event schema");
            if (!consumerKeys.Add(consumer.ConsumerKey))
                throw Invalid(module.Id, $"duplicate installed consumer key '{consumer.ConsumerKey}'");
            RequireKey(consumer.OperationKey, module.Id, $"operation for consumer '{consumer.ConsumerKey}'");
            RequireVersion(consumer.OperationVersion, module.Id, $"operation for consumer '{consumer.ConsumerKey}'");
            RequireKey(consumer.SystemGrantKey, module.Id, $"system grant for consumer '{consumer.ConsumerKey}'");

            if (!operations.TryGetValue(consumer.OperationKey, out ModuleOperationDescriptor? operation)
                || operation.ModuleId != module.Id
                || operation.OperationVersion != consumer.OperationVersion
                || operation.ExecutionKind != ModuleExecutionKind.CommittedEvent
                || operation.SystemGrantKey != consumer.SystemGrantKey)
                throw Invalid(module.Id, $"consumer '{consumer.ConsumerKey}' does not resolve to its owned committed-event operation and grant");
            if (!grants.TryGetValue(consumer.SystemGrantKey, out ModuleSystemGrantDescriptor? grant)
                || grant.OwnerModuleId != module.Id
                || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal))
                throw Invalid(module.Id, $"consumer '{consumer.ConsumerKey}' references an unregistered compatible system grant");

            foreach (ModuleEventVersion accepted in consumer.AcceptedEvents)
            {
                RequireKey(accepted.EventKey, module.Id, $"accepted event for '{consumer.ConsumerKey}'");
                RequireVersion(accepted.SchemaVersion, module.Id, $"accepted event '{accepted.EventKey}'");
                if (!events.TryGetValue((accepted.EventKey, accepted.SchemaVersion), out IntegrationEventDescriptor? eventDescriptor))
                    throw Invalid(module.Id, $"consumer '{consumer.ConsumerKey}' references uninstalled event schema '{accepted.EventKey}' version {accepted.SchemaVersion}");
                if (eventDescriptor.OwnerModuleId != module.Id
                    && !module.Requires.Contains(eventDescriptor.OwnerModuleId, StringComparer.Ordinal)
                    && !module.OptionalDependencies.Contains(eventDescriptor.OwnerModuleId, StringComparer.Ordinal))
                    throw Invalid(module.Id, $"consumer '{consumer.ConsumerKey}' must declare a dependency on event owner '{eventDescriptor.OwnerModuleId}'");
            }
        }

        foreach ((ModuleDescriptor module, ModuleRecurringScheduleDescriptor schedule) in schedules)
        {
            RequireOwner(module.Id, schedule.OwnerModuleId, $"recurring schedule '{schedule.ScheduleKey}'");
            RequireKey(schedule.ScheduleKey, module.Id, "recurring schedule");
            if (!scheduleKeys.Add(schedule.ScheduleKey))
                throw Invalid(module.Id, $"duplicate installed recurring schedule key '{schedule.ScheduleKey}'");
            RequireKey(schedule.JobKey, module.Id, $"job for schedule '{schedule.ScheduleKey}'");
            RequireVersion(schedule.JobVersion, module.Id, $"job for schedule '{schedule.ScheduleKey}'");
            RequireKey(schedule.SystemGrantKey, module.Id, $"system grant for schedule '{schedule.ScheduleKey}'");
            if (string.IsNullOrWhiteSpace(schedule.UtcSchedule) || schedule.UtcSchedule.Length > 120
                || schedule.UtcSchedule.Contains('\r') || schedule.UtcSchedule.Contains('\n'))
                throw Invalid(module.Id, $"recurring schedule '{schedule.ScheduleKey}' requires a bounded UTC schedule");
            if (!Enum.IsDefined(schedule.MisfirePolicy))
                throw Invalid(module.Id, $"recurring schedule '{schedule.ScheduleKey}' has an unknown misfire policy");
            if (!jobs.TryGetValue((schedule.JobKey, schedule.JobVersion), out ModuleJobDescriptor? job)
                || job.ModuleId != module.Id
                || job.ExecutionKind != ModuleExecutionKind.ScheduledSystem
                || !operations.TryGetValue(job.OperationKey, out ModuleOperationDescriptor? operation)
                || operation.SystemGrantKey != schedule.SystemGrantKey
                || operation.ExecutionKind != ModuleExecutionKind.ScheduledSystem)
                throw Invalid(module.Id, $"recurring schedule '{schedule.ScheduleKey}' does not resolve to an owned scheduled-system job and grant");
        }

    }

    private static void RequireOwner(string moduleId, string owner, string subject)
    {
        if (owner != moduleId)
            throw Invalid(moduleId, $"{subject} must be owned by its declaring module");
    }

    private static void RequireKey(string? value, string moduleId, string subject)
    {
        if (value is null || value.Length > 120 || !StableKey.IsMatch(value))
            throw Invalid(moduleId, $"{subject} requires a stable lower-case key no longer than 120 characters");
    }

    private static void RequireVersion(int version, string moduleId, string subject)
    {
        if (version <= 0)
            throw Invalid(moduleId, $"{subject} requires a positive version");
    }

    private static void RequireKind(ModuleExecutionKind kind, string moduleId, string subject)
    {
        if (!Enum.IsDefined(kind))
            throw Invalid(moduleId, $"{subject} has an unknown execution kind");
    }

    private static void RequirePayloadLimit(int limit, string moduleId, string subject)
    {
        if (limit <= 0 || limit > HostMaximumPayloadBytes)
            throw Invalid(moduleId, $"{subject} payload limit must be between 1 and {HostMaximumPayloadBytes} bytes");
    }

    private static void RequireUnique(IEnumerable<string> values, string moduleId, string subject)
    {
        string? duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw Invalid(moduleId, $"duplicate {subject} '{duplicate}'");
    }

    private static void RequireFiniteUniqueKeys(IReadOnlyList<string> values, string moduleId, string subject)
    {
        if (values.Count == 0)
            throw Invalid(moduleId, $"{subject} must be finite and nonempty");
        RequireUnique(values, moduleId, subject);
        foreach (string value in values)
            RequireKey(value, moduleId, subject);
    }

    private static InvalidOperationException Invalid(string moduleId, string detail) =>
        new($"Trykatch module '{moduleId}' {detail}.");
}
