using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Common;
using Trykatch.Application.Execution;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task PersistedProjectsCreateWorkCanRetryAfterWritesFeatureIsEnabled()
    {
        SeededWork work = await fixture.SeedProjectsCreateWorkAsync();
        await fixture.SetProjectsWritesOverrideAsync(work.OrganizationId, enabled: false);

        ModuleExecutionOutcome disabled = await fixture.ExecuteAsync(work);

        disabled.State.ShouldBe(ModuleExecutionState.RetryableFailure);
        disabled.FailureCode.ShouldBe("execution_failed");
        ProjectsWorkEffects noEffects = await fixture.ReadProjectsWorkEffectsAsync(work);
        noEffects.ShouldBe(new ProjectsWorkEffects(0, 0, 0, 0, 0));

        await fixture.SetProjectsWritesOverrideAsync(work.OrganizationId, enabled: true);
        await fixture.MakeDueAsync(work);
        ModuleExecutionOutcome enabled = await fixture.ExecuteAsync(work);

        enabled.State.ShouldBe(ModuleExecutionState.Completed);
        ProjectsWorkEffects completedEffects = await fixture.ReadProjectsWorkEffectsAsync(work);
        completedEffects.Projects.ShouldBe(1);
        completedEffects.ProjectAudits.ShouldBe(1);
        completedEffects.OutboxMessages.ShouldBe(2);
        completedEffects.Completions.ShouldBe(1);
        completedEffects.AuditIntents.ShouldBeGreaterThan(0);

        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.ReadProjectsWorkEffectsAsync(work)).ShouldBe(completedEffects);
    }

    private sealed record ProjectsWorkEffects(
        long Projects,
        long ProjectAudits,
        long OutboxMessages,
        long Completions,
        long AuditIntents);

    private sealed partial class ExecutionFixture
    {
        private static readonly JsonSerializerOptions ProjectsWebJson = new(JsonSerializerDefaults.Web);

        public async Task<SeededWork> SeedProjectsCreateWorkAsync()
        {
            SeededOrganization seeded = await SeedOrganizationAsync(["projects.manage", "projects.read"]);
            CreateProjectCommand command = new("Persisted feature-gated project", "Seeded as real queued Projects work.");
            string payload = JsonSerializer.Serialize(command, ProjectsWebJson);
            Guid workId = Guid.CreateVersion7();
            ModuleExecutionIdentity identity = new(seeded.OrganizationId, seeded.ActorId, seeded.ActorId, seeded.MembershipId,
                ModuleExecutionKind.UserCommand, null, null, null, ModuleOperationRegistry.CatalogVersion, null);

            await using ModuleWorkDbContext db = new(new DbContextOptionsBuilder<ModuleWorkDbContext>().UseNpgsql(owner).Options);
            db.ModuleWorkItems.Add(new ModuleWorkItem
            {
                Id = workId,
                OrganizationId = seeded.OrganizationId,
                ActorId = seeded.ActorId,
                RequestId = Guid.CreateVersion7(),
                ExecutionIdentityKey = ModuleJobIntent.RequestIdentity(seeded.ActorId),
                JobKey = "projects.create",
                JobVersion = 1,
                OperationKey = "projects.create",
                OperationVersion = 1,
                ContextVersion = 1,
                ExecutionContextJson = JsonSerializer.Serialize(new ModuleWorkEnvelope(1, "projects", identity), ProjectsWebJson),
                Payload = payload,
                PayloadHash = OutboxPayloadHash.Compute(payload),
                IntentHash = ModuleJobIntent.IntentHash(payload, "enqueue", null),
                SubmissionMode = "enqueue",
                State = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return new(workId, seeded.OrganizationId, seeded.ActorId, seeded.ActorId, "projects.create", Guid.Empty);
        }

        public async Task SetProjectsWritesOverrideAsync(Guid organizationId, bool enabled)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                INSERT INTO platform.organization_feature_overrides
                    ("OrganizationId", "Environment", "FeatureKey", "Enabled", "Version")
                VALUES (@organization, 'production', 'projects.writes', @enabled, @version)
                ON CONFLICT ("OrganizationId", "Environment", "FeatureKey") DO UPDATE
                SET "Enabled"=EXCLUDED."Enabled", "Version"=EXCLUDED."Version"
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("enabled", enabled);
            command.Parameters.AddWithValue("version", Guid.NewGuid());
            await command.ExecuteNonQueryAsync();
        }

        public async Task<ProjectsWorkEffects> ReadProjectsWorkEffectsAsync(SeededWork work)
        {
            long projects = await CountOwnerAsync(
                "SELECT count(*) FROM app.projects WHERE \"OrganizationId\"=@organization", ("organization", work.OrganizationId));
            long audits = await CountOwnerAsync(
                "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\"=@organization AND \"Action\" LIKE 'project.%'",
                ("organization", work.OrganizationId));
            long outbox = await CountOwnerAsync("""
                SELECT count(*) FROM platform.outbox_messages
                WHERE ("Type"='TrykatchApp.Application.Projects.ProjectChanged'
                       AND "Payload"->>'organizationId'=@organization)
                   OR ("Type"='projects.created' AND "ContextJson"->>'organizationId'=@organization)
                """, ("organization", work.OrganizationId.ToString()));
            long completions = await CountOwnerAsync(
                "SELECT count(*) FROM app.module_work_completions WHERE \"OrganizationId\"=@organization AND \"WorkId\"=@work",
                ("organization", work.OrganizationId), ("work", work.WorkId));
            long auditIntents = await CountOwnerAsync(
                "SELECT count(*) FROM platform.audit_intents WHERE \"OrganizationId\"=@organization", ("organization", work.OrganizationId));
            return new(projects, audits, outbox, completions, auditIntents);
        }

    }
}
