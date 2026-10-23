using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Outbox;
using Trykatch.Identity;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class OutboxMigrationTests
{
    private static readonly JsonSerializerOptions ContextJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ContextMigrationPreservesLegacyHistoryAndNewMetadataSurvivesJsonbRoundTrip()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string owner = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
        await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
            await identity.Database.MigrateAsync();
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
            await platform.Database.MigrateAsync();
        await using (ApplicationDbContext previous = CreateContext(owner))
            await previous.GetService<IMigrator>().MigrateAsync("20260911210000_AddOutboxRecovery");
        Guid legacyId = Guid.NewGuid();
        await using (NpgsqlConnection connection = new(owner))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand insert = new("""
                INSERT INTO platform.outbox_messages ("Id", "Type", "Payload", "OccurredAt", "ProcessedAt", "Attempts", "ReplayGeneration")
                VALUES (@id, 'legacy.contract', '{}', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 3, 2)
                """, connection);
            insert.Parameters.AddWithValue("id", legacyId);
            await insert.ExecuteNonQueryAsync();
        }
        Guid newId = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        const string payload = "{\"z\":[1.00,2e2],\"a\":{\"long\":\"x\",\"b\":true}}";
        string hash = OutboxPayloadHash.Compute(payload);
        OutboxContext provenance = new(Guid.NewGuid(), actor, actor, Guid.NewGuid(), "UserCommand",
            "kernel.probe", 1, "kernel.probe-committed", 1, new string('A', 64), null);
        await using (ApplicationDbContext current = CreateContext(owner))
        {
            await current.Database.MigrateAsync();
            current.OutboxMessages.Add(new OutboxMessage
            {
                Id = newId, Type = provenance.EventKey, Payload = payload, ContextVersion = 1,
                ContextJson = JsonSerializer.Serialize(provenance, ContextJsonOptions), PayloadHash = hash
            });
            await current.SaveChangesAsync();
        }
        await using ApplicationDbContext read = CreateContext(owner);
        OutboxMessage legacy = await read.OutboxMessages.SingleAsync(row => row.Id == legacyId);
        legacy.ProcessedAt.ShouldNotBeNull();
        legacy.Attempts.ShouldBe(3);
        legacy.ReplayGeneration.ShouldBe(2);
        OutboxContextReader.TryRead(legacy, out OutboxContext? legacyContext, out bool isLegacy).ShouldBeTrue();
        isLegacy.ShouldBeTrue();
        legacyContext.ShouldBeNull();
        OutboxMessage stored = await read.OutboxMessages.SingleAsync(row => row.Id == newId);
        stored.Payload.ShouldNotBe(payload); // PostgreSQL JSONB normalizes object order/spacing/numbers.
        OutboxContextReader.TryRead(stored, out OutboxContext? context, out bool newIsLegacy).ShouldBeTrue();
        newIsLegacy.ShouldBeFalse();
        context.ShouldBe(provenance);
        stored.PayloadHash.ShouldBe(hash);
    }
}
