using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Jobs;

internal sealed class PostgresModuleJobScheduler(
    ModuleJobRegistry registry,
    IOrganizationContext organization,
    OrganizationControlPlaneDbContext controlPlane,
    ApplicationDbContext application,
    IOrganizationAccessResolver accessResolver,
    IConfiguration configuration,
    TimeProvider timeProvider) : IModuleJobScheduler
{
    private readonly Dictionary<string, ModuleWorkItem> pending = new(StringComparer.Ordinal);
    private DbTransaction? pendingTransaction;

    public Task<ModuleWorkReceipt> EnqueueAsync(
        ModuleJobRequest request,
        CancellationToken cancellationToken = default) => SubmitAsync(request, "enqueue", null, cancellationToken);

    public Task<ModuleWorkReceipt> ScheduleAsync(
        ModuleJobRequest request,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken = default) => SubmitAsync(request, "schedule", dueAtUtc, cancellationToken);

    private async Task<ModuleWorkReceipt> SubmitAsync(
        ModuleJobRequest request,
        string mode,
        DateTimeOffset? dueAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!configuration.GetValue<bool>("Jobs:Enabled"))
            return Receipt(Guid.Empty, ModuleWorkState.Unavailable, "jobs_unavailable");
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty
            || request.RequestId == Guid.Empty || request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !registry.TryGet(request.JobKey, request.JobVersion, out ModuleJobRegistration? registration)
            || registration is null
            || registration.Operation.Descriptor.ExecutionKind != ModuleExecutionKind.UserCommand)
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "invalid_request");

        Guid resolvedMembershipId;
        try
        {
            resolvedMembershipId = organization.MembershipId;
        }
        catch (OrganizationMembershipRequiredException)
        {
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "not_authorized");
        }
        if (resolvedMembershipId == Guid.Empty)
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "not_authorized");

        IDbContextTransaction controlTransaction = controlPlane.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Job submission requires an active organization transaction.");
        DbTransaction dbTransaction = controlTransaction.GetDbTransaction();
        TrackTransaction(dbTransaction);

        string identityKey = ModuleJobIntent.RequestIdentity(organization.ActorId);
        string requestScope = string.Join("/", organization.OrganizationId.ToString("N"), identityKey,
            request.JobKey, request.JobVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), request.RequestId.ToString("N"));
        await controlPlane.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({requestScope}, 0))",
            cancellationToken);

        OrganizationAccess? currentAccess = await accessResolver.ResolveAsync(
            organization.ActorId, organization.OrganizationId, cancellationToken);
        if (currentAccess is null
            || currentAccess.OrganizationId != organization.OrganizationId
            || currentAccess.ActorId != organization.ActorId
            || currentAccess.MembershipId == Guid.Empty
            || currentAccess.MembershipId != resolvedMembershipId
            || registration.Operation.Descriptor.RequiredPermissionKeys.Any(permission => !currentAccess.Permissions.Contains(permission)))
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "not_authorized");

        if (application.Database.CurrentTransaction is null)
        {
            _ = await OrganizationTransactionEnlistment.EnlistAsync(
                controlPlane, application, currentAccess.OrganizationId, currentAccess.ActorId, cancellationToken);
        }
        else if (!ReferenceEquals(application.Database.CurrentTransaction.GetDbTransaction(), dbTransaction))
        {
            throw new InvalidOperationException("Application data must share the active organization transaction.");
        }

        JsonElement payload = request.Payload;
        string payloadText = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(payloadText) > registration.Descriptor.MaximumPayloadBytes)
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "invalid_payload");

        DateTimeOffset? normalizedDue = dueAtUtc is { } due ? ModuleJobIntent.NormalizeDue(due) : null;
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        ModuleExecutionIdentity identity = new(
            currentAccess.OrganizationId,
            currentAccess.ActorId,
            currentAccess.ActorId,
            currentAccess.MembershipId,
            ModuleExecutionKind.UserCommand,
            null,
            null,
            null,
            ModuleOperationRegistry.CatalogVersion,
            null);

        ModuleWorkItem candidate;
        try
        {
            candidate = ModuleJobIntent.CreateWork(registration, currentAccess.OrganizationId, identity,
                request.RequestId, identityKey, payloadText, mode, normalizedDue, now);
        }
        catch (ArgumentException)
        {
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "invalid_payload");
        }
        catch (System.Text.Json.JsonException)
        {
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "invalid_payload");
        }
        catch (InvalidOperationException)
        {
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "invalid_payload");
        }

        ModuleOperationContext policyContext = new(candidate.Id, currentAccess.OrganizationId, currentAccess.ActorId,
            currentAccess.ActorId, ModuleExecutionKind.UserCommand, candidate.OperationKey, candidate.OperationVersion);
        bool authorized = await registration.Operation.ResourcePolicy.AuthorizeAsync(policyContext, payload, cancellationToken);
        if (!authorized)
            return Receipt(Guid.Empty, ModuleWorkState.Rejected, "not_authorized");

        string pendingKey = ReceiptKey(candidate);
        if (pending.TryGetValue(pendingKey, out ModuleWorkItem? pendingWork))
            return SameIntent(pendingWork, candidate)
                ? ExistingReceipt(pendingWork)
                : Receipt(Guid.Empty, ModuleWorkState.Rejected, "request_id_conflict");

        await using ModuleWorkDbContext coordinator = CreateCoordinatorContext(configuration);
        WorkReceiptProjection? existing = await coordinator.ModuleWorkItems.AsNoTracking()
            .Where(work => work.OrganizationId == currentAccess.OrganizationId
                && work.ExecutionIdentityKey == identityKey
                && work.JobKey == candidate.JobKey
                && work.JobVersion == candidate.JobVersion
                && work.RequestId == candidate.RequestId)
            .Select(work => new WorkReceiptProjection(
                work.Id, work.PayloadHash, work.IntentHash, work.SubmissionMode, work.DueAtUtc, work.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return SameIntent(existing, candidate)
                ? ExistingReceipt(existing)
                : Receipt(Guid.Empty, ModuleWorkState.Rejected, "request_id_conflict");
        }

        _ = await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO infrastructure.module_work_items
                ("Id", "OrganizationId", "ActorId", "RequestId", "ExecutionIdentityKey", "JobKey", "JobVersion",
                 "OperationKey", "OperationVersion", "ContextVersion", "ExecutionContextJson", "Payload", "PayloadHash",
                 "IntentHash", "SubmissionMode", "DueAtUtc", "NextAttemptAtUtc", "OperatorPaused", "RecoveryGeneration",
                 "ScheduleKey", "ScheduleTargetKey", "OccurrenceAtUtc", "State", "Attempts", "LeaseId", "LeaseUntilUtc",
                 "LastFailureCode", "CreatedAtUtc")
            VALUES
                ({candidate.Id}, {candidate.OrganizationId}, {candidate.ActorId}, {candidate.RequestId}, {candidate.ExecutionIdentityKey},
                 {candidate.JobKey}, {candidate.JobVersion}, {candidate.OperationKey}, {candidate.OperationVersion}, {candidate.ContextVersion},
                 CAST({candidate.ExecutionContextJson} AS jsonb), CAST({candidate.Payload} AS jsonb), {candidate.PayloadHash}, {candidate.IntentHash},
                 {candidate.SubmissionMode}, {candidate.DueAtUtc}, {candidate.NextAttemptAtUtc}, {candidate.OperatorPaused}, {candidate.RecoveryGeneration},
                 {candidate.ScheduleKey}, {candidate.ScheduleTargetKey}, {candidate.OccurrenceAtUtc}, {candidate.State}, {candidate.Attempts},
                 {candidate.LeaseId}, {candidate.LeaseUntilUtc}, {candidate.LastFailureCode}, {candidate.CreatedAtUtc})
            """, cancellationToken);

        pending.Add(pendingKey, candidate);
        return Receipt(candidate.Id, mode == "enqueue" ? ModuleWorkState.Accepted : ModuleWorkState.Scheduled, acceptedAtUtc: candidate.CreatedAtUtc);
    }

    private void TrackTransaction(DbTransaction transaction)
    {
        if (ReferenceEquals(pendingTransaction, transaction)) return;
        pending.Clear();
        pendingTransaction = transaction;
    }

    private static string ReceiptKey(ModuleWorkItem work) =>
        string.Join('\0', work.OrganizationId.ToString("N"), work.ExecutionIdentityKey, work.JobKey,
            work.JobVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), work.RequestId.ToString("N"));

    private static bool SameIntent(ModuleWorkItem existing, ModuleWorkItem requested) =>
        string.Equals(existing.PayloadHash, requested.PayloadHash, StringComparison.Ordinal)
        && string.Equals(existing.IntentHash, requested.IntentHash, StringComparison.Ordinal)
        && string.Equals(existing.SubmissionMode, requested.SubmissionMode, StringComparison.Ordinal)
        && existing.DueAtUtc == requested.DueAtUtc;

    private static bool SameIntent(WorkReceiptProjection existing, ModuleWorkItem requested) =>
        string.Equals(existing.PayloadHash, requested.PayloadHash, StringComparison.Ordinal)
        && string.Equals(existing.IntentHash, requested.IntentHash, StringComparison.Ordinal)
        && string.Equals(existing.SubmissionMode, requested.SubmissionMode, StringComparison.Ordinal)
        && existing.DueAtUtc == requested.DueAtUtc;

    private static ModuleWorkReceipt ExistingReceipt(ModuleWorkItem work) => ExistingReceipt(
        new WorkReceiptProjection(work.Id, work.PayloadHash, work.IntentHash, work.SubmissionMode, work.DueAtUtc, work.CreatedAtUtc));

    private static ModuleWorkReceipt ExistingReceipt(WorkReceiptProjection work) =>
        Receipt(work.Id, ModuleWorkState.AlreadyAccepted, acceptedAtUtc: work.CreatedAtUtc);

    private static ModuleWorkReceipt Receipt(
        Guid workId,
        ModuleWorkState state,
        string? failureCode = null,
        DateTimeOffset? acceptedAtUtc = null) => new(workId, state, acceptedAtUtc, failureCode);

    private static ModuleWorkDbContext CreateCoordinatorContext(IConfiguration configuration)
    {
        string connectionString = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);
        DbContextOptions<ModuleWorkDbContext> options = new DbContextOptionsBuilder<ModuleWorkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ModuleWorkDbContext(options);
    }

    private sealed record WorkReceiptProjection(
        Guid Id,
        string PayloadHash,
        string IntentHash,
        string SubmissionMode,
        DateTimeOffset? DueAtUtc,
        DateTimeOffset CreatedAtUtc);
}
