using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task OrganizationRequestCounterHasForcedRlsAndOnlyItsExactOrganizationProfile()
    {
        ProjectsSeededUser first = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        ProjectsSeededUser second = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        foreach (ProjectsSeededUser user in new[] { first, second })
        {
            await using MutationScope request = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
            (await CreateRequestAdmission(request.Services, 1, 60).TryAdmitAsync()).Disposition
                .ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
        }

        await using NpgsqlConnection owner = new(fixture.OwnerConnectionForRequestAdmission);
        await owner.OpenAsync();
        await using (NpgsqlCommand rls = new("""
            SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
            WHERE oid='app.organization_request_limits'::regclass
            """, owner))
            (await rls.ExecuteScalarAsync()).ShouldBe(true);
        foreach (string role in new[] { PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.WorkCoordinatorRole })
        foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
        {
            await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, 'app.organization_request_limits', @privilege)", owner);
            grant.Parameters.AddWithValue("role", role);
            grant.Parameters.AddWithValue("privilege", privilege);
            (await grant.ExecuteScalarAsync()).ShouldBe(role == PostgresRuntimeRoleFixture.OrganizationRole
                && privilege is "SELECT" or "INSERT" or "UPDATE", $"Unexpected {privilege} for {role}.");
        }

        IConfiguration configuration = environment.Services.GetRequiredService<IConfiguration>();
        await using NpgsqlConnection runtime = new(RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Organization));
        await runtime.OpenAsync();
        await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
        await using (NpgsqlCommand scope = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", runtime, transaction))
        {
            scope.Parameters.AddWithValue("organization", first.OrganizationId.ToString());
            scope.Parameters.AddWithValue("actor", first.ActorId.ToString());
            await scope.ExecuteNonQueryAsync();
        }
        await using (NpgsqlCommand visible = new("SELECT count(*) FROM app.organization_request_limits", runtime, transaction))
            (await visible.ExecuteScalarAsync()).ShouldBe(1L);
        await using (NpgsqlCommand foreign = new("UPDATE app.organization_request_limits SET \"Attempts\"=1 WHERE \"OrganizationId\"=@foreign", runtime, transaction))
        {
            foreign.Parameters.AddWithValue("foreign", second.OrganizationId);
            (await foreign.ExecuteNonQueryAsync()).ShouldBe(0);
        }
        await using NpgsqlCommand delete = new("DELETE FROM app.organization_request_limits", runtime, transaction);
        (await Should.ThrowAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
