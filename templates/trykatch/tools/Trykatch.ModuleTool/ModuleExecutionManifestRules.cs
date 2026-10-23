using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

/// <summary>Validates declarative execution metadata without loading module assemblies.</summary>
internal static class ModuleExecutionManifestRules
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly Regex StableKey = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "user-command", "committed-event", "scheduled-system" };
    private static readonly HashSet<string> MisfirePolicies = new(StringComparer.Ordinal) { "coalesce", "catch-up" };

    internal static void Validate(IReadOnlyCollection<ModuleWorkspace.LoadedModule> modules, List<string> errors)
    {
        var enabled = modules.Where(module => module.Registration.Enabled).ToArray();
        var registeredPermissions = enabled.SelectMany(module => module.Manifest.Contributions.Permissions).ToHashSet(StringComparer.Ordinal);
        var operations = new Dictionary<string, (string Owner, ModuleOperationManifest Value)>(StringComparer.Ordinal);
        var grants = new Dictionary<string, (string Owner, ModuleSystemGrantManifest Value)>(StringComparer.Ordinal);
        var events = new Dictionary<(string Key, int Version), string>();
        var jobs = new Dictionary<(string Key, int Version), (string Owner, ModuleJobManifest Value)>();
        var consumers = new List<(ModuleWorkspace.LoadedModule Module, ModuleEventConsumerManifest Value)>();
        var schedules = new List<(ModuleWorkspace.LoadedModule Module, ModuleRecurringScheduleManifest Value)>();

        foreach (var module in enabled)
        {
            var manifest = module.Manifest;
            var execution = manifest.Execution;
            string id = manifest.Id;
            bool hasWork = execution.Operations.Count + execution.SystemGrants.Count + execution.IntegrationEvents.Count
                + execution.EventConsumers.Count + execution.Jobs.Count + execution.RecurringSchedules.Count > 0;
            if (hasWork && !manifest.Capabilities.Contains("background-work", StringComparer.Ordinal))
                Error(id, "declares background work without the BackgroundWork capability");

            foreach (var operation in execution.Operations)
            {
                Key(id, operation.OperationKey, "operation", errors); Version(id, operation.OperationVersion, "operation", errors);
                Key(id, operation.PayloadSchemaKey, "payload schema", errors); Version(id, operation.PayloadSchemaVersion, "payload schema", errors);
                Key(id, operation.ResourcePolicyKey, "resource policy", errors); Payload(id, operation.MaximumPayloadBytes, errors);
                if (!Kinds.Contains(operation.ExecutionKind)) Error(id, $"operation '{operation.OperationKey}' has an unknown execution kind");
                Unique(id, operation.RequiredPermissionKeys, "required permission", errors);
                foreach (string permission in operation.RequiredPermissionKeys)
                {
                    Key(id, permission, "required permission", errors);
                    if (!registeredPermissions.Contains(permission)) Error(id, $"operation '{operation.OperationKey}' requires unregistered permission '{permission}'");
                }
                if (operation.ExecutionKind == "user-command" && operation.SystemGrantKey is not null)
                    Error(id, $"user operation '{operation.OperationKey}' cannot declare a system grant");
                if (operation.ExecutionKind is "committed-event" or "scheduled-system")
                {
                    Key(id, operation.SystemGrantKey, "system grant", errors);
                    if (!execution.SystemGrants.Any(grant => grant.GrantKey == operation.SystemGrantKey)) Error(id, $"operation '{operation.OperationKey}' references an unregistered system grant '{operation.SystemGrantKey}'");
                }
                if (!operations.TryAdd(operation.OperationKey, (id, operation))) Error(id, $"duplicate installed operation key '{operation.OperationKey}'");
            }
            foreach (var grant in execution.SystemGrants)
            {
                Key(id, grant.GrantKey, "system grant", errors); Version(id, grant.GrantVersion, "system grant", errors);
                if (grant.AuditActorId == Guid.Empty) Error(id, $"system grant '{grant.GrantKey}' requires a nonempty audit actor id");
                FiniteKeys(id, grant.AllowedOperationKeys, "operation set", errors); FiniteKeys(id, grant.AllowedResourceKeys, "resource set", errors);
                foreach (string resource in grant.AllowedResourceKeys)
                    if (manifest.DataOwnership?.Resources.All(item => item.Name != resource) != false) Error(id, $"system grant '{grant.GrantKey}' references undeclared resource '{resource}'");
                if (!grants.TryAdd(grant.GrantKey, (id, grant))) Error(id, $"duplicate installed system grant key '{grant.GrantKey}'");
            }
            foreach (var integrationEvent in execution.IntegrationEvents)
            {
                Key(id, integrationEvent.EventKey, "event", errors); Version(id, integrationEvent.SchemaVersion, "event", errors); Payload(id, integrationEvent.MaximumPayloadBytes, errors);
                if (!events.TryAdd((integrationEvent.EventKey, integrationEvent.SchemaVersion), id)) Error(id, $"duplicate installed event schema '{integrationEvent.EventKey}' version {integrationEvent.SchemaVersion}");
            }
            consumers.AddRange(execution.EventConsumers.Select(value => (module, value)));
            foreach (var job in execution.Jobs)
            {
                Key(id, job.JobKey, "job", errors); Version(id, job.JobVersion, "job", errors); Key(id, job.OperationKey, "job operation", errors); Version(id, job.OperationVersion, "job operation", errors);
                Key(id, job.PayloadSchemaKey, "job payload schema", errors); Version(id, job.PayloadSchemaVersion, "job payload schema", errors); Payload(id, job.MaximumPayloadBytes, errors);
                if (!Kinds.Contains(job.ExecutionKind)) Error(id, $"job '{job.JobKey}' has an unknown execution kind");
                if (!jobs.TryAdd((job.JobKey, job.JobVersion), (id, job))) Error(id, $"duplicate installed job '{job.JobKey}' version {job.JobVersion}");
            }
            schedules.AddRange(execution.RecurringSchedules.Select(value => (module, value)));
        }

        foreach (var (owner, grant) in grants.Values)
        foreach (string operationKey in grant.AllowedOperationKeys)
        {
            if (!operations.TryGetValue(operationKey, out var operation) || operation.Owner != owner || operation.Value.SystemGrantKey != grant.GrantKey)
                Error(owner, $"system grant '{grant.GrantKey}' references an operation it does not own: '{operationKey}'");
            else if (!grant.AllowedResourceKeys.Contains(operation.Value.ResourcePolicyKey, StringComparer.Ordinal))
                Error(owner, $"system grant '{grant.GrantKey}' does not allow operation '{operationKey}' resource policy '{operation.Value.ResourcePolicyKey}'");
        }
        foreach (var (owner, operation) in operations.Values)
        {
            if (operation.ExecutionKind == "user-command") continue;
            if (operation.SystemGrantKey is null || !grants.TryGetValue(operation.SystemGrantKey, out var grant) || grant.Owner != owner
                || !grant.Value.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
                || !grant.Value.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal))
                Error(owner, $"operation '{operation.OperationKey}' has no matching finite system grant for its operation and resource");
        }
        foreach (var (owner, job) in jobs.Values)
        {
            if (!operations.TryGetValue(job.OperationKey, out var operation) || operation.Owner != owner
                || operation.Value.OperationVersion != job.OperationVersion || operation.Value.ExecutionKind != job.ExecutionKind
                || operation.Value.PayloadSchemaKey != job.PayloadSchemaKey || operation.Value.PayloadSchemaVersion != job.PayloadSchemaVersion
                || job.MaximumPayloadBytes > operation.Value.MaximumPayloadBytes)
                Error(owner, $"job '{job.JobKey}' does not resolve to a compatible owned operation and payload schema");
        }
        var consumerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, consumer) in consumers)
        {
            string id = module.Manifest.Id;
            Key(id, consumer.ConsumerKey, "event consumer", errors);
            if (!consumerKeys.Add(consumer.ConsumerKey)) Error(id, $"duplicate installed consumer key '{consumer.ConsumerKey}'");
            if (consumer.AcceptedEvents.Count == 0) Error(id, $"event consumer '{consumer.ConsumerKey}' must accept at least one event schema");
            Unique(id, consumer.AcceptedEvents.Select(item => $"{item.EventKey}/{item.SchemaVersion}"), "accepted event", errors);
            Key(id, consumer.OperationKey, "consumer operation", errors); Version(id, consumer.OperationVersion, "consumer operation", errors); Key(id, consumer.SystemGrantKey, "consumer system grant", errors);
            if (!operations.TryGetValue(consumer.OperationKey, out var operation) || operation.Owner != id || operation.Value.OperationVersion != consumer.OperationVersion
                || operation.Value.ExecutionKind != "committed-event" || operation.Value.SystemGrantKey != consumer.SystemGrantKey)
                Error(id, $"consumer '{consumer.ConsumerKey}' does not resolve to its owned committed-event operation and grant");
            if (consumer.SystemGrantKey is null || !grants.TryGetValue(consumer.SystemGrantKey, out var grant) || grant.Owner != id
                || !grant.Value.AllowedOperationKeys.Contains(consumer.OperationKey, StringComparer.Ordinal))
                Error(id, $"consumer '{consumer.ConsumerKey}' references an unregistered compatible system grant");
            foreach (var accepted in consumer.AcceptedEvents)
            {
                Key(id, accepted.EventKey, "accepted event", errors); Version(id, accepted.SchemaVersion, "accepted event", errors);
                if (!events.TryGetValue((accepted.EventKey, accepted.SchemaVersion), out string? eventOwner)) Error(id, $"consumer '{consumer.ConsumerKey}' references uninstalled event schema '{accepted.EventKey}' version {accepted.SchemaVersion}");
                else if (eventOwner != id && !module.Manifest.Requires.Contains(eventOwner, StringComparer.Ordinal) && !module.Manifest.OptionalDependencies.Contains(eventOwner, StringComparer.Ordinal))
                    Error(id, $"consumer '{consumer.ConsumerKey}' must declare a dependency on event owner '{eventOwner}'");
            }
        }
        var scheduleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, schedule) in schedules)
        {
            string id = module.Manifest.Id;
            Key(id, schedule.ScheduleKey, "recurring schedule", errors);
            if (!scheduleKeys.Add(schedule.ScheduleKey)) Error(id, $"duplicate installed recurring schedule key '{schedule.ScheduleKey}'");
            Key(id, schedule.JobKey, "scheduled job", errors); Version(id, schedule.JobVersion, "scheduled job", errors); Key(id, schedule.SystemGrantKey, "schedule system grant", errors);
            if (string.IsNullOrWhiteSpace(schedule.UtcSchedule) || schedule.UtcSchedule.Length > 120 || schedule.UtcSchedule.Contains('\r') || schedule.UtcSchedule.Contains('\n')) Error(id, $"recurring schedule '{schedule.ScheduleKey}' requires a bounded UTC schedule");
            if (!MisfirePolicies.Contains(schedule.MisfirePolicy)) Error(id, $"recurring schedule '{schedule.ScheduleKey}' has an unknown misfire policy");
            if (!jobs.TryGetValue((schedule.JobKey, schedule.JobVersion), out var job) || job.Owner != id || job.Value.ExecutionKind != "scheduled-system"
                || !operations.TryGetValue(job.Value.OperationKey, out var operation) || operation.Value.SystemGrantKey != schedule.SystemGrantKey || operation.Value.ExecutionKind != "scheduled-system")
                Error(id, $"recurring schedule '{schedule.ScheduleKey}' does not resolve to an owned scheduled-system job and grant");
        }

        void Error(string id, string message) => errors.Add($"Trykatch module '{id}' {message}.");
    }

    private static void Key(string module, string? value, string subject, List<string> errors)
    { if (value is null || value.Length > 120 || !StableKey.IsMatch(value)) errors.Add($"Trykatch module '{module}' {subject} requires a stable lower-case key no longer than 120 characters."); }
    private static void Version(string module, int value, string subject, List<string> errors)
    { if (value <= 0) errors.Add($"Trykatch module '{module}' {subject} requires a positive version."); }
    private static void Payload(string module, int value, List<string> errors)
    { if (value <= 0 || value > MaximumPayloadBytes) errors.Add($"Trykatch module '{module}' payload limit must be between 1 and {MaximumPayloadBytes} bytes."); }
    private static void Unique(string module, IEnumerable<string> values, string subject, List<string> errors)
    { string? duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1)?.Key; if (duplicate is not null) errors.Add($"Trykatch module '{module}' duplicate {subject} '{duplicate}'."); }
    private static void FiniteKeys(string module, List<string> values, string subject, List<string> errors)
    { if (values.Count == 0) errors.Add($"Trykatch module '{module}' {subject} must be finite and nonempty."); Unique(module, values, subject, errors); foreach (string value in values) Key(module, value, subject, errors); }

}
