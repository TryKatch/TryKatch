using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trykatch.Application.Auditing;
using Trykatch.Application.Execution;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Eventing;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

/// <summary>Runs one persisted operation in a fresh, actor-scoped organization transaction.</summary>
internal sealed partial class OrganizationWorkExecutor(
    IServiceScopeFactory scopeFactory, IModuleWorkStore store, TimeProvider timeProvider, ILogger<OrganizationWorkExecutor> logger) : IOrganizationWorkExecutor
{
    private static readonly JsonSerializerOptions ContextOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(1);

    public async Task<ModuleExecutionOutcome> ExecuteAsync(ModuleExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedOperationKey)
            || request.ExpectedOperationVersion < 1)
            return Terminal("invalid_request");

        ModuleWorkClaim claim = await store.ClaimAsync(request, cancellationToken);
        if (claim.Outcome is not null) return claim.Outcome;
        ModuleWorkItem work = claim.Work ?? throw new InvalidOperationException("Claim has no work.");
        Guid leaseId = claim.LeaseId ?? throw new InvalidOperationException("Claim has no lease.");
        using CancellationTokenSource deadline = new(ExecutionTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ModuleExecutionOutcome outcome;
        try
        {
            outcome = await ExecuteClaimAsync(work, claim.RecoveryOnly, linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(work.Id, leaseId);
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = Retry("execution_timeout");
        }
        catch (PostgresException exception) when (exception.SqlState is "42501" or "42P01" or "42703" or "28P01" or "28000")
        {
            outcome = Terminal("runtime_database_contract_invalid");
        }
        catch (Exception exception)
        {
            LogExecutionFailure(logger, work.Id, exception.GetType().FullName ?? exception.GetType().Name);
            outcome = Retry("execution_failed");
        }

        // A cancelled acknowledgement is uncertain, but a committed completion marker prevents another effect.
        try
        {
            bool acknowledged = await store.FinishAsync(work.Id, leaseId, outcome, cancellationToken);
            return acknowledged ? outcome : Retry("lease_lost");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(work.Id, leaseId);
            throw;
        }
        catch (Exception)
        {
            return Retry("acknowledgement_failed");
        }
    }

    private async Task<ModuleExecutionOutcome> ExecuteClaimAsync(ModuleWorkItem work, bool recoveryOnly, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(work.ExecutionContextJson) > 16_384)
            return Terminal("work_context_invalid");
        ModuleWorkEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<ModuleWorkEnvelope>(work.ExecutionContextJson, ContextOptions); }
        catch (JsonException) { return Terminal("work_context_invalid"); }
        if (envelope is null || envelope.Version != 1 || work.ContextVersion != 1
            || envelope.Identity is not { } identity || string.IsNullOrWhiteSpace(envelope.ModuleId)
            || !IsPersistedIdentityWellFormed(identity)
            || identity.TraceCorrelation is { Length: > 128 }
            || identity.CatalogVersion != ModuleOperationRegistry.CatalogVersion
            || work.Id == Guid.Empty || work.OrganizationId == Guid.Empty || work.ActorId == Guid.Empty
            || identity.OrganizationId != work.OrganizationId || identity.EffectiveActorId != work.ActorId)
            return Terminal("work_context_invalid");

        return await ExecuteWithDenialAuditAsync(new OrganizationOperationInvocation(work.Id, work.OrganizationId, work.ActorId,
            work.OperationKey, work.OperationVersion, work.Payload, work.PayloadHash, envelope.ModuleId, identity), recoveryOnly, ct);
    }

    internal async Task<ModuleExecutionOutcome> ExecuteEventAsync(ModuleEventDelivery delivery, bool recoveryOnly, CancellationToken ct)
    {
        if (delivery.Id == Guid.Empty || delivery.MessageId == Guid.Empty || delivery.OrganizationId == Guid.Empty
            || delivery.EffectiveActorId == Guid.Empty || delivery.OriginActorId == Guid.Empty
            || delivery.OperationVersion < 1 || delivery.SchemaVersion < 1 || delivery.GrantVersion < 1
            || string.IsNullOrWhiteSpace(delivery.ConsumerModuleId) || string.IsNullOrWhiteSpace(delivery.ConsumerKey)
            || string.IsNullOrWhiteSpace(delivery.OperationKey) || string.IsNullOrWhiteSpace(delivery.GrantKey))
            return Terminal("delivery_context_invalid");
        await using AsyncServiceScope sourceScope = scopeFactory.CreateAsyncScope();
        OutboxMessage? source = await sourceScope.ServiceProvider.GetRequiredService<OutboxDbContext>().Messages.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == delivery.MessageId, ct);
        if (source is null || source.ProcessedAt is null
            || !OutboxContextReader.TryRead(source, out OutboxContext? context, out bool legacy) || legacy || context is null
            || context.OrganizationId != delivery.OrganizationId || context.OriginActorId != delivery.OriginActorId
            || context.EventKey != delivery.EventKey || context.SchemaVersion != delivery.SchemaVersion
            || source.PayloadHash != delivery.PayloadHash || source.ReplayGeneration != delivery.ReplayGeneration)
            return Terminal("source_event_invalid");
        ModuleExecutionIdentity identity = new(delivery.OrganizationId, delivery.EffectiveActorId, delivery.OriginActorId, null,
            ModuleExecutionKind.CommittedEvent, delivery.GrantKey, delivery.GrantVersion, delivery.MessageId,
            ModuleOperationRegistry.CatalogVersion, context.CorrelationId);
        return await ExecuteWithDenialAuditAsync(new OrganizationOperationInvocation(delivery.Id, delivery.OrganizationId,
            delivery.EffectiveActorId, delivery.OperationKey, delivery.OperationVersion, source.Payload, delivery.PayloadHash,
            delivery.ConsumerModuleId, identity, delivery, source), recoveryOnly, ct);
    }

    private async Task<ModuleExecutionOutcome> ExecuteWithDenialAuditAsync(OrganizationOperationInvocation work, bool recoveryOnly, CancellationToken ct)
    {
        try { return await ExecuteInvocationAsync(work, recoveryOnly, ct); }
        catch (ExecutionDeniedException denied) { return await RecordDeniedAsync(work, denied.Code, ct); }
        catch (OrganizationMembershipRequiredException) { return await RecordDeniedAsync(work, "membership_required", ct); }
    }

    private async Task<ModuleExecutionOutcome> ExecuteInvocationAsync(OrganizationOperationInvocation work, bool recoveryOnly, CancellationToken ct)
    {
        ModuleExecutionIdentity identity = work.Identity;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        JsonDocument payload;
        try
        {
            if (Encoding.UTF8.GetByteCount(work.Payload) > 1_048_576
                || !string.Equals(work.PayloadHash, OutboxPayloadHash.Compute(work.Payload), StringComparison.OrdinalIgnoreCase))
                return Terminal("payload_invalid");
            payload = JsonDocument.Parse(work.Payload);
        }
        catch (JsonException) { return Terminal("payload_invalid"); }
        using (payload)
        {
            OrganizationControlPlaneDbContext control = services.GetRequiredService<OrganizationControlPlaneDbContext>();
            await using IOrganizationDataScope transaction = await services.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(work.OrganizationId, work.ActorId, ct);
            ApplicationDbContext application = services.GetRequiredService<ApplicationDbContext>();
            await using var enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
                control, application, work.OrganizationId, work.ActorId, ct);
            if (control.Database.CreateExecutionStrategy().RetriesOnFailure || application.Database.CreateExecutionStrategy().RetriesOnFailure)
                return Terminal("automatic_retry_not_supported");
            string lockKey = work.Delivery is { } eventDelivery
                ? $"event/{work.OrganizationId:N}/{eventDelivery.MessageId:N}/{eventDelivery.ConsumerKey}"
                : $"{work.OrganizationId:N}/{work.Id:N}";
            await application.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 20261023))", ct);

            // Recovery reads only the host marker under RLS, before current authorization. It never reruns a handler.
            if (work.Delivery is { } snapshot)
            {
                ModuleEventInbox? inbox = await application.Set<ModuleEventInbox>().IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.MessageId == snapshot.MessageId
                        && row.ConsumerKey == snapshot.ConsumerKey, ct);
                if (inbox is not null)
                    return inbox.ActorId == work.ActorId && inbox.PayloadHash == work.PayloadHash
                        && inbox.OperationKey == work.OperationKey && inbox.OperationVersion == work.OperationVersion
                        && inbox.SchemaVersion == snapshot.SchemaVersion
                        ? new(ModuleExecutionState.AlreadyCompleted) : Terminal("inbox_mismatch");
            }
            else
            {
                ModuleWorkCompletion? completed = await application.Set<ModuleWorkCompletion>().IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.WorkId == work.Id, ct);
                if (completed is not null)
                    return completed.OperationKey == work.OperationKey && completed.OperationVersion == work.OperationVersion
                        && completed.ActorId == work.ActorId && completed.PayloadHash == work.PayloadHash
                        ? new(ModuleExecutionState.AlreadyCompleted) : Terminal("completion_mismatch");
            }

            if (recoveryOnly) return Terminal("attempts_exhausted");

            ModuleOperationRegistry registry = services.GetRequiredService<ModuleOperationRegistry>();
            if (!registry.ContainsModule(work.ModuleId))
                return new(ModuleExecutionState.Paused, "module_unavailable");
            ModuleEventConsumerRegistration? consumer = null;
            if (work.Delivery is { } eventSnapshot)
            {
                ModuleEventRegistry events = services.GetRequiredService<ModuleEventRegistry>();
                if (!events.TryGet(eventSnapshot.ConsumerKey, out consumer) || consumer is null)
                    return new(ModuleExecutionState.Paused, "consumer_unavailable");
                if (consumer.Descriptor.OwnerModuleId != work.ModuleId || consumer.Operation.OperationKey != work.OperationKey
                    || consumer.Operation.OperationVersion != work.OperationVersion || consumer.Grant.GrantKey != identity.GrantKey
                    || consumer.Grant.GrantVersion != identity.GrantVersion || consumer.Grant.AuditActorId != work.ActorId
                    || !consumer.Descriptor.AcceptedEvents.Any(accepted => accepted.EventKey == eventSnapshot.EventKey
                        && accepted.SchemaVersion == eventSnapshot.SchemaVersion)
                    || work.Source is null || !OutboxContextReader.TryRead(work.Source, out OutboxContext? sourceContext, out _)
                    || sourceContext is null || !events.TryValidateSource(sourceContext, work.Payload, out _, out _))
                    return Terminal("consumer_registration_invalid");
            }
            if (!registry.TryGet(work.ModuleId, work.OperationKey, work.OperationVersion, out ModuleOperationRegistration? registration)
                || registration is null)
                return Terminal("operation_unavailable");
            if (work.Delivery is null && registration.Handler is null) return Terminal("operation_adapter_unavailable");
            ModuleOperationDescriptor operation = registration.Descriptor;
            if (identity.ExecutionKind != operation.ExecutionKind)
                return Terminal("execution_kind_invalid");
            bool user = operation.ExecutionKind == ModuleExecutionKind.UserCommand;
            if (user ? !IsUser(identity) : !SystemWorkGrantValidator.TryValidate(registration, identity))
                return new(ModuleExecutionState.Denied, "execution_identity_invalid");
            if (Encoding.UTF8.GetByteCount(work.Payload) > operation.MaximumPayloadBytes)
                return Terminal("payload_invalid");

            Organization? organization = await control.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == work.OrganizationId, ct);
            if (organization is null) return Terminal("organization_unavailable");
            if (!organization.IsActive) return new(ModuleExecutionState.Paused, "organization_inactive");

            OrganizationContext context = services.GetRequiredService<OrganizationContext>();
            if (user)
            {
                OrganizationAccess? access = await services.GetRequiredService<IOrganizationAccessResolver>()
                    .ResolveAsync(work.ActorId, work.OrganizationId, ct);
                if (access is null) throw new ExecutionDeniedException("membership_required");
                if (operation.RequiredPermissionKeys.Any(permission => !access.Permissions.Contains(permission)))
                    throw new ExecutionDeniedException("permission_denied");
                context.Initialize(access);
            }
            else
            {
                context.InitializeSystem(new(work.OrganizationId, organization.Slug, work.ActorId,
                    operation.RequiredPermissionKeys.ToHashSet(StringComparer.Ordinal)));
            }

            OrganizationDataRoute route;
            try { route = await services.GetRequiredService<IOrganizationDataPlacement>().ResolveAsync(work.OrganizationId, ct); }
            catch (KeyNotFoundException) { return new(ModuleExecutionState.Paused, "placement_unavailable"); }
            catch (InvalidOperationException) { return new(ModuleExecutionState.Paused, "placement_unavailable"); }
            if (route.OrganizationId != work.OrganizationId || route.Placement != OrganizationDataPlacementKind.Shared
                || route.Provider != "postgres" || route.DatabaseIdentifier != control.Database.GetDbConnection().Database)
                return Terminal("placement_not_supported");

            if (operation.ExecutionKind == ModuleExecutionKind.CommittedEvent)
            {
                OutboxMessage? source = work.Source ?? await services.GetRequiredService<OutboxDbContext>().Messages.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == identity.SourceMessageId, ct);
                if (source is null || !OutboxContextReader.TryRead(source, out OutboxContext? sourceContext, out bool legacy)
                    || legacy || sourceContext is null || sourceContext.OrganizationId != identity.OrganizationId
                    || sourceContext.OriginActorId != identity.OriginActorId || source.PayloadHash != work.PayloadHash
                    || !registry.AcceptsSourceEvent(registration, sourceContext.EventKey, sourceContext.SchemaVersion))
                    return Terminal("source_event_invalid");
            }

            services.GetRequiredService<ModuleWorkExecutionContext>().Initialize(identity, operation);
            ModuleOperationContext operationContext = new(work.Id, work.OrganizationId, work.ActorId, identity.OriginActorId,
                identity.ExecutionKind, work.OperationKey, work.OperationVersion);
            if (!await registration.ResourcePolicy.AuthorizeAsync(operationContext, payload.RootElement, ct))
                throw new ExecutionDeniedException("resource_denied");
            if (work.Delivery is { } delivery && consumer is not null && work.Source is { } committedSource)
            {
                await consumer.Consumer.HandleAsync(new(delivery.MessageId, work.OrganizationId, work.ActorId, identity.OriginActorId,
                    delivery.EventKey, delivery.SchemaVersion, committedSource.OccurredAt, identity.TraceCorrelation), payload.RootElement.Clone(), ct);
                application.Set<ModuleEventInbox>().Add(new()
                {
                    OrganizationId = work.OrganizationId, MessageId = delivery.MessageId, ConsumerKey = delivery.ConsumerKey,
                    SchemaVersion = delivery.SchemaVersion, ActorId = work.ActorId, PayloadHash = work.PayloadHash,
                    OperationKey = work.OperationKey, OperationVersion = work.OperationVersion, ProcessedAtUtc = timeProvider.GetUtcNow()
                });
            }
            else
            {
                await registration.Handler!.HandleAsync(operationContext, payload.RootElement.Clone(), ct);
                application.Set<ModuleWorkCompletion>().Add(new()
                {
                    OrganizationId = work.OrganizationId, WorkId = work.Id, ActorId = work.ActorId,
                    OperationKey = work.OperationKey, OperationVersion = work.OperationVersion,
                    PayloadHash = work.PayloadHash, CompletedAtUtc = timeProvider.GetUtcNow()
                });
            }
            control.AuditIntents.Add(CreateAudit(work, identity, "completed"));
            await application.SaveChangesAsync(ct);
            await control.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return new(ModuleExecutionState.Completed);
        }
    }

    private async Task<ModuleExecutionOutcome> RecordDeniedAsync(OrganizationOperationInvocation work, string code, CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(work.OrganizationId, work.ActorId, ct);
            control.AuditIntents.Add(CreateAudit(work, work.Identity, code));
            await control.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(ModuleExecutionState.Denied, code);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Retry("denial_audit_failed"); }
    }

    private AuditIntent CreateAudit(OrganizationOperationInvocation work, ModuleExecutionIdentity identity, string status) => AuditIntent.Create(
        Guid.CreateVersion7(), work.OrganizationId, work.ActorId, work.Delivery is null
            ? status == "completed" ? "module.work.completed" : "module.work.denied"
            : status == "completed" ? "module.event.completed" : "module.event.denied",
        new AuditTarget(work.Delivery is null ? "module-work" : "module-event-delivery", work.Id.ToString(), "Module work"),
        new Dictionary<string, string?>
        {
            ["status"] = status,
            ["originActorId"] = identity.OriginActorId?.ToString(),
            ["executionKind"] = identity.ExecutionKind.ToString(),
            ["systemActor"] = identity.ExecutionKind == ModuleExecutionKind.UserCommand ? null : identity.GrantKey
        }, timeProvider.GetUtcNow());

    private async Task ReleaseAsync(Guid workId, Guid leaseId)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
        try { await store.ReleaseAsync(workId, leaseId, cleanup.Token); }
        catch (Exception) { /* An unreleased claim expires; no unbounded cleanup or stale acknowledgement. */ }
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Error,
        Message = "Module work {WorkId} failed with {ExceptionType}")]
    private static partial void LogExecutionFailure(ILogger logger, Guid workId, string exceptionType);

    private ModuleExecutionOutcome Retry(string code) => new(ModuleExecutionState.RetryableFailure, code,
        timeProvider.GetUtcNow() + ModuleWorkStore.MinimumRetryDelay);
    private static ModuleExecutionOutcome Terminal(string code) => new(ModuleExecutionState.TerminalFailure, code);
    private static bool IsPersistedIdentityWellFormed(ModuleExecutionIdentity identity) => identity.ExecutionKind switch
    {
        ModuleExecutionKind.UserCommand => IsUser(identity),
        ModuleExecutionKind.CommittedEvent => IsSystemShape(identity) && identity.SourceMessageId is { } source && source != Guid.Empty,
        ModuleExecutionKind.ScheduledSystem => IsSystemShape(identity) && identity.SourceMessageId is null,
        _ => false
    };
    private static bool IsSystemShape(ModuleExecutionIdentity identity) => identity.MembershipId is null
        && identity.OriginActorId != Guid.Empty && !string.IsNullOrWhiteSpace(identity.GrantKey) && identity.GrantVersion > 0;
    private static bool IsUser(ModuleExecutionIdentity identity) => identity.MembershipId is { } membership && membership != Guid.Empty
        && identity.OriginActorId == identity.EffectiveActorId && identity.GrantKey is null && identity.GrantVersion is null
        && identity.SourceMessageId is null;
    private sealed record OrganizationOperationInvocation(Guid Id, Guid OrganizationId, Guid ActorId, string OperationKey,
        int OperationVersion, string Payload, string PayloadHash, string ModuleId, ModuleExecutionIdentity Identity,
        ModuleEventDelivery? Delivery = null, OutboxMessage? Source = null);

    private sealed class ExecutionDeniedException(string code) : Exception { public string Code { get; } = code; }
}

/// <summary>Validates explicit adapters at startup; it does not schedule or dispatch work.</summary>
internal sealed class ModuleWorkStartupValidator(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<ModuleOperationRegistry>();
        _ = scope.ServiceProvider.GetRequiredService<ModuleEventRegistry>();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
