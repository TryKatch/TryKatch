using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Modules;
using Trykatch.Identity;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class QuotaUploadPolicyUpgradeIntegrationTests
{
    [TestMethod]
    public async Task ExistingUnboundUploadRetainsItsHoldAndCannotAcquireInventedPolicyAuthority()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options))
            await identity.Database.MigrateAsync();
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options))
            await platform.Database.MigrateAsync();
        ServiceCollection services = new();
        ModuleCatalog catalog = services.AddModules(new ConfigurationBuilder().Build(), EnabledModules.All);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using (ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            provider.GetServices<IApplicationModelContributor>(), moduleCatalog: catalog))
            await application.GetService<IMigrator>().MigrateAsync("20261024205627_AddOrganizationQuotas");

        Guid organization = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        Guid reservation = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await using NpgsqlConnection owner = new(connectionString);
        await owner.OpenAsync();
        await using (NpgsqlCommand seed = new("""
            INSERT INTO app.organization_entitlements
              ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version", "Source",
               "SourceRevision", "SourceHash", "EffectiveAtUtc")
            VALUES (@organization, 'legacyquota.storage-bytes', 'legacyquota', 1, 0, NULL, @version,
                    'legacy-fixture', 0, @hash, CURRENT_TIMESTAMP);
            INSERT INTO app.organization_quota_reservations
              ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
               "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            VALUES (@organization, @reservation, 'legacyquota.storage-bytes', @actor, 42, @operation, @version,
                    2, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + interval '15 minutes', NULL);
            INSERT INTO app.module_upload_operations
              ("OrganizationId", "ResourceKey", "OperationId", "ActorId", "MembershipId", "ReservationId", "SubjectId",
               "ObjectKey", "StorageNamespaceFingerprint", "Length", "Sha256", "MetadataFingerprint", "State",
               "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            VALUES (@organization, 'legacyquota.storage-bytes', @operation, @actor, @membership, @reservation, @subject,
                    @key, @hash, 42, @hash, @hash, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + interval '15 minutes', NULL);
            """, owner))
        {
            seed.Parameters.AddWithValue("organization", organization);
            seed.Parameters.AddWithValue("operation", operation);
            seed.Parameters.AddWithValue("reservation", reservation);
            seed.Parameters.AddWithValue("actor", actor);
            seed.Parameters.AddWithValue("membership", Guid.NewGuid());
            seed.Parameters.AddWithValue("subject", Guid.NewGuid());
            seed.Parameters.AddWithValue("version", Guid.NewGuid());
            seed.Parameters.AddWithValue("key", "legacy-upload-key");
            seed.Parameters.AddWithValue("hash", new string('a', 64));
            await seed.ExecuteNonQueryAsync();
        }
        var migrated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connectionString);
        migrated.Exit.ShouldBe(0, migrated.Error + migrated.Output);
        await using (NpgsqlCommand read = new("""
            SELECT upload."UploadPolicyKey", upload."UploadPolicyVersion", upload."State", hold."State", hold."Units"
            FROM app.module_upload_operations upload
            JOIN app.organization_quota_reservations hold
              ON hold."OrganizationId" = upload."OrganizationId" AND hold."Id" = upload."ReservationId"
            WHERE upload."OrganizationId" = @organization AND upload."OperationId" = @operation
            """, owner))
        {
            read.Parameters.AddWithValue("organization", organization);
            read.Parameters.AddWithValue("operation", operation);
            await using NpgsqlDataReader row = await read.ExecuteReaderAsync();
            (await row.ReadAsync()).ShouldBeTrue();
            row.IsDBNull(0).ShouldBeTrue();
            row.IsDBNull(1).ShouldBeTrue();
            row.GetInt32(2).ShouldBe(0);
            row.GetInt32(3).ShouldBe(0);
            row.GetInt64(4).ShouldBe(42);
        }
        await using NpgsqlCommand promote = new("""
            UPDATE app.module_upload_operations SET "UploadPolicyKey" = 'legacyquota.upload', "UploadPolicyVersion" = 1
            WHERE "OrganizationId" = @organization AND "OperationId" = @operation
            """, owner);
        promote.Parameters.AddWithValue("organization", organization);
        promote.Parameters.AddWithValue("operation", operation);
        (await Should.ThrowAsync<PostgresException>(() => promote.ExecuteNonQueryAsync())).SqlState
            .ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
