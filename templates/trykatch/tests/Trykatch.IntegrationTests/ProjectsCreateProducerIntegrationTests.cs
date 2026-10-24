using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.IntegrationEvents;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private static readonly JsonSerializerOptions ProducerJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedProjectEventTypes = ["TrykatchApp.Application.Projects.ProjectChanged", "projects.created"];

    [TestMethod]
    public async Task ProjectsCreateCommitsLegacyAndTypedEventsWithAuditAtomically()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        CreateProjectCommand command = new("Acceptance project", "Transactional creation");

        Result<ProjectDto> result = await fixture.RunProjectsCreateAsync(user, command, commit: true);

        result.IsSuccess.ShouldBeTrue();
        ProjectProducerSnapshot stored = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        stored.ProjectCount.ShouldBe(1);
        stored.AuditCount.ShouldBe(1);
        stored.OutboxRows.Count.ShouldBe(2);
        stored.OutboxRows.Select(row => row.Type).Order(StringComparer.Ordinal)
            .ShouldBe(ExpectedProjectEventTypes.Order(StringComparer.Ordinal));

        ProjectProducerRow legacy = stored.OutboxRows.Single(row => row.Type == "TrykatchApp.Application.Projects.ProjectChanged");
        legacy.ContextVersion.ShouldBeNull();
        legacy.ContextJson.ShouldBeNull();
        using JsonDocument legacyPayload = JsonDocument.Parse(legacy.Payload);
        legacyPayload.RootElement.GetProperty("organizationId").GetGuid().ShouldBe(user.OrganizationId);
        legacyPayload.RootElement.GetProperty("projectId").GetGuid().ShouldBe(result.Value!.Id);
        legacyPayload.RootElement.GetProperty("actorId").GetGuid().ShouldBe(user.ActorId);
        legacyPayload.RootElement.GetProperty("change").GetString().ShouldBe("created");

        ProjectProducerRow typed = stored.OutboxRows.Single(row => row.Type == "projects.created");
        typed.ContextVersion.ShouldBe(1);
        typed.PayloadHash.ShouldBe(OutboxPayloadHash.Compute(typed.Payload));
        OutboxContext context = JsonSerializer.Deserialize<OutboxContext>(typed.ContextJson!, ProducerJson)!;
        context.OrganizationId.ShouldBe(user.OrganizationId);
        context.EffectiveActorId.ShouldBe(user.ActorId);
        context.OriginActorId.ShouldBe(user.ActorId);
        context.MembershipId.ShouldBe(user.MembershipId);
        context.ExecutionKind.ShouldBe("UserCommand");
        context.OperationKey.ShouldBe("projects.create");
        context.OperationVersion.ShouldBe(1);
        context.EventKey.ShouldBe("projects.created");
        context.SchemaVersion.ShouldBe(1);
        JsonSerializer.Deserialize<ProjectCreated>(typed.Payload, ProducerJson)
            .ShouldBe(new ProjectCreated(user.OrganizationId, result.Value.Id, user.ActorId));
    }

    [TestMethod]
    public async Task ProjectsCreateDenialAndValidationFailureWriteNoProjectAuditOrOutboxRows()
    {
        ProjectsSeededUser deniedUser = await fixture.SeedProjectsUserAsync([]);
        ProjectsSeededUser invalidUser = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);

        Result<ProjectDto> denied = await fixture.RunProjectsCreateAsync(
            deniedUser, new CreateProjectCommand("Denied", null), commit: true);
        Result<ProjectDto> invalid = await fixture.RunProjectsCreateAsync(
            invalidUser, new CreateProjectCommand("  ", null), commit: true);

        denied.ErrorCode.ShouldBe("forbidden");
        invalid.ErrorCode.ShouldBe("validation");
        (await fixture.ReadProjectProducerSnapshotAsync(deniedUser.OrganizationId)).ShouldBeEmpty();
        (await fixture.ReadProjectProducerSnapshotAsync(invalidUser.OrganizationId)).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ProjectsCreateOutboxAndAuditRollBackWithTheEnclosingOrganizationTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);

        Result<ProjectDto> result = await fixture.RunProjectsCreateAsync(
            user, new CreateProjectCommand("Rolled back", "No partial source rows"), commit: false);

        result.IsSuccess.ShouldBeTrue();
        (await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId)).ShouldBeEmpty();
    }

    private sealed record ProjectsSeededUser(Guid OrganizationId, Guid ActorId, Guid MembershipId);

    private sealed partial class ExecutionFixture
    {
        public async Task<ProjectsSeededUser> SeedProjectsUserAsync(string[] permissions)
        {
            SeededOrganization seed = await SeedOrganizationAsync(permissions);
            return new(seed.OrganizationId, seed.ActorId, seed.MembershipId);
        }

        public async Task<Result<ProjectDto>> RunProjectsCreateAsync(
            ProjectsSeededUser user,
            CreateProjectCommand command,
            bool commit)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider
                .GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(user.OrganizationId, user.ActorId, CancellationToken.None);
            OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
                control, application, user.OrganizationId, user.ActorId, CancellationToken.None);
            OrganizationAccess access = (await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                .ResolveAsync(user.ActorId, user.OrganizationId, CancellationToken.None))!;
            scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);

            Result<ProjectDto> result = await scope.ServiceProvider.GetRequiredService<ProjectUseCases>()
                .CreateAsync(command, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().SaveChangesAsync(CancellationToken.None);
            if (commit)
                await transaction.CommitAsync(CancellationToken.None);
            return result;
        }

        public async Task<ProjectProducerSnapshot> ReadProjectProducerSnapshotAsync(Guid organizationId)
        {
            long projects = await ScalarLongAsync(
                "SELECT count(*) FROM app.projects WHERE \"OrganizationId\"=@organization", organizationId);
            long audits = await ScalarLongAsync(
                "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\"=@organization AND \"Action\"=@operation",
                organizationId, ("operation", AuditActions.ProjectCreated));
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "Type", "Payload"::text, "ContextVersion", "ContextJson"::text, "PayloadHash"
                FROM platform.outbox_messages
                WHERE "Type" IN (@legacy, @typed)
                  AND (("Type"=@legacy AND "Payload"->>'organizationId'=@organization)
                    OR ("Type"=@typed AND "ContextJson"->>'organizationId'=@organization))
                ORDER BY "Type"
                """, connection);
            command.Parameters.AddWithValue("legacy", "TrykatchApp.Application.Projects.ProjectChanged");
            command.Parameters.AddWithValue("typed", "projects.created");
            command.Parameters.AddWithValue("organization", organizationId.ToString());
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<ProjectProducerRow> rows = [];
            while (await reader.ReadAsync())
                rows.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            return new(projects, audits, rows);
        }

        private async Task<long> ScalarLongAsync(
            string sql,
            Guid organizationId,
            (string Name, object Value)? extra = null)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            if (extra is { } parameter)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }

    private sealed record ProjectProducerRow(
        string Type,
        string Payload,
        int? ContextVersion,
        string? ContextJson,
        string? PayloadHash);

    private sealed record ProjectProducerSnapshot(
        long ProjectCount,
        long AuditCount,
        IReadOnlyList<ProjectProducerRow> OutboxRows)
    {
        public void ShouldBeEmpty()
        {
            ProjectCount.ShouldBe(0);
            AuditCount.ShouldBe(0);
            OutboxRows.ShouldBeEmpty();
        }
    }
}
