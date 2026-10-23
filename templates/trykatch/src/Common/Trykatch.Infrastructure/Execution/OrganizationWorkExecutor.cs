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
            outcome = await ExecuteWithDenialAuditAsync(work, claim.RecoveryOnly, linked.Token);
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

    private async Task<ModuleExecutionOutcome> ExecuteWithDenialAuditAsync(ModuleWorkItem work, bool recoveryOnly, CancellationToken ct)
    {
        try { return await ExecuteClaimAsync(work, recoveryOnly, ct); }
        catch (ExecutionDeniedException denied) { return await RecordDeniedAsync(work, denied.Code, ct); }
        catch (OrganizationMembershipRequiredException) { return await RecordDeniedAsync(work, "membership_required", ct); }
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
            string lockKey = $"{work.OrganizationId:N}/{work.Id:N}";
            await application.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 20261023))", ct);

            // Recovery reads only the host marker under RLS, before current authorization. It never reruns a handler.
            ModuleWorkCompletion? completed = await application.Set<ModuleWorkCompletion>().IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.WorkId == work.Id, ct);
            if (completed is not null)
                return completed.OperationKey == work.OperationKey && completed.OperationVersion == work.OperationVersion
                    && completed.ActorId == work.ActorId && completed.PayloadHash == work.PayloadHash
                    ? new(ModuleExecutionState.AlreadyCompleted)
                    : Terminal("completion_mismatch");

            if (recoveryOnly) return Terminal("attempts_exhausted");

            ModuleOperationRegistry registry = services.GetRequiredService<ModuleOperationRegistry>();
            if (!registry.ContainsModule(envelope.ModuleId))
                return new(ModuleExecutionState.Paused, "module_unavailable");
            if (!registry.TryGet(envelope.ModuleId, work.OperationKey, work.OperationVersion, out ModuleOperationRegistration? registration)
                || registration is null)
                return Terminal("operation_unavailable");
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
                OutboxMessage? source = await services.GetRequiredService<OutboxDbContext>().Messages.AsNoTracking()
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
            await registration.Handler.HandleAsync(operationContext, payload.RootElement, ct);
            application.Set<ModuleWorkCompletion>().Add(new()
            {
                OrganizationId = work.OrganizationId, WorkId = work.Id, ActorId = work.ActorId,
                OperationKey = work.OperationKey, OperationVersion = work.OperationVersion,
                PayloadHash = work.PayloadHash, CompletedAtUtc = timeProvider.GetUtcNow()
            });
            control.AuditIntents.Add(CreateAudit(work, identity, "completed"));
            await application.SaveChangesAsync(ct);
            await control.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return new(ModuleExecutionState.Completed);
        }
    }

    private async Task<ModuleExecutionOutcome> RecordDeniedAsync(ModuleWorkItem work, string code, CancellationToken ct)
    {
        try
        {
            ModuleWorkEnvelope envelope = JsonSerializer.Deserialize<ModuleWorkEnvelope>(work.ExecutionContextJson, ContextOptions)!;
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(work.OrganizationId, work.ActorId, ct);
            control.AuditIntents.Add(CreateAudit(work, envelope.Identity, code));
            await control.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(ModuleExecutionState.Denied, code);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Retry("denial_audit_failed"); }
    }

    private AuditIntent CreateAudit(ModuleWorkItem work, ModuleExecutionIdentity identity, string status) => AuditIntent.Create(
        Guid.CreateVersion7(), work.OrganizationId, work.ActorId, status == "completed" ? "module.work.completed" : "module.work.denied",
        new AuditTarget("module-work", work.Id.ToString(), "Module work"),
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
    private sealed class ExecutionDeniedException(string code) : Exception { public string Code { get; } = code; }
}

/// <summary>Validates explicit adapters at startup; it does not schedule or dispatch work.</summary>
internal sealed class ModuleWorkStartupValidator(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<ModuleOperationRegistry>();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
