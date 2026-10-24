using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Caching;

internal readonly record struct CacheReadGeneration(bool CanCache, Guid Generation);

internal sealed class CacheGenerationStore(
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext control,
    IOrganizationContext organization)
{
    private const string InvalidScope = "Organization cache operations require the current actor-scoped organization transaction.";

    internal DbTransaction Transaction => SharedTransaction() ?? throw new InvalidOperationException(InvalidScope);

    public async Task<CacheReadGeneration> ReadAsync(string logicalHash, CancellationToken cancellationToken = default)
    {
        ValidateScopeAndKey(logicalHash);
        DbTransaction? transaction = SharedTransaction();
        if (transaction is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted) return default;

        await using NpgsqlCommand command = CreateCommand(transaction, """
            SELECT current_setting('transaction_read_only') = 'on',
                   current_setting('app.organization_id', true), current_setting('app.actor_id', true),
                   (SELECT "Generation" FROM app.cache_invalidations
                    WHERE "OrganizationId" = @organization AND "KeyHash" = @hash)
            """);
        command.Parameters.AddWithValue("organization", organization.OrganizationId);
        command.Parameters.AddWithValue("hash", logicalHash);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException(InvalidScope);
        ValidateDatabaseScope(reader, 1, 2);
        return reader.GetBoolean(0)
            && !application.ChangeTracker.Entries().Any()
            && !control.ChangeTracker.Entries().Any()
            ? new(true, reader.IsDBNull(3) ? Guid.Empty : reader.GetGuid(3))
            : default;
    }

    internal bool CanPublishSourceResult()
    {
        // Explicit detection also covers factories that disable automatic change detection.
        application.ChangeTracker.DetectChanges();
        control.ChangeTracker.DetectChanges();
        return !application.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            && !control.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    internal JsonSerializerOptions CreatePayloadSerializerOptions()
    {
        Type[] entityTypes = application.Model.GetEntityTypes().Concat(control.Model.GetEntityTypes())
            .Select(entity => entity.ClrType).Distinct().ToArray();
        // The resolver captures only type metadata, never a scoped DbContext or tracked graph.
        return CachePayloadCodec.CreateSerializerOptions(type =>
        {
            if (entityTypes.Any(entity => entity.IsAssignableFrom(type))
                || typeof(global::Trykatch.Domain.Common.Entity).IsAssignableFrom(type)
                || typeof(DbContext).IsAssignableFrom(type)
                || typeof(IQueryable).IsAssignableFrom(type)
                || typeof(IOrganizationContext).IsAssignableFrom(type)
                || type == typeof(OrganizationAccess))
                throw new NotSupportedException("Organization cache payloads must contain detached read DTOs.");
        });
    }

    public async Task<Guid> InvalidateAsync(string logicalHash, CancellationToken cancellationToken = default)
    {
        ValidateScopeAndKey(logicalHash);
        DbTransaction transaction = Transaction;
        // Serialize even the first insertion, before reading the old generation. Advisory locks
        // are transaction-scoped and use only an opaque key; no new connection or role is opened.
        await using (NpgsqlCommand acquire = CreateCommand(transaction, """
            SELECT pg_advisory_xact_lock(hashtextextended(@lock, 20261024)),
                   current_setting('app.organization_id', true), current_setting('app.actor_id', true)
            """))
        {
            acquire.Parameters.AddWithValue("lock", "org-cache:" + organization.OrganizationId.ToString("N") + ":" + logicalHash);
            await using NpgsqlDataReader reader = await acquire.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException(InvalidScope);
            ValidateDatabaseScope(reader, 1, 2);
        }

        Guid previous;
        await using (NpgsqlCommand read = CreateCommand(transaction, """
            SELECT "Generation" FROM app.cache_invalidations
            WHERE "OrganizationId" = @organization AND "KeyHash" = @hash
            """))
        {
            AddKey(read, logicalHash);
            previous = (Guid)(await read.ExecuteScalarAsync(cancellationToken) ?? Guid.Empty);
        }
        await using NpgsqlCommand replace = CreateCommand(transaction, """
            INSERT INTO app.cache_invalidations ("OrganizationId", "KeyHash", "Generation", "UpdatedAtUtc")
            VALUES (@organization, @hash, @generation, CURRENT_TIMESTAMP)
            ON CONFLICT ("OrganizationId", "KeyHash") DO UPDATE
            SET "Generation" = EXCLUDED."Generation", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
            """);
        AddKey(replace, logicalHash);
        replace.Parameters.AddWithValue("generation", Guid.NewGuid());
        if (await replace.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException(InvalidScope);
        return previous;
    }

    private void AddKey(NpgsqlCommand command, string logicalHash)
    {
        command.Parameters.AddWithValue("organization", organization.OrganizationId);
        command.Parameters.AddWithValue("hash", logicalHash);
    }

    private DbTransaction? SharedTransaction()
    {
        DbTransaction? controlTransaction = control.Database.CurrentTransaction?.GetDbTransaction();
        DbTransaction? applicationTransaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (controlTransaction is null || applicationTransaction is null) return null;
        if (!ReferenceEquals(controlTransaction, applicationTransaction)
            || !ReferenceEquals(control.Database.GetDbConnection(), application.Database.GetDbConnection()))
            throw new InvalidOperationException(InvalidScope);
        return controlTransaction;
    }

    private NpgsqlCommand CreateCommand(DbTransaction transaction, string sql) =>
        new(sql, (NpgsqlConnection)control.Database.GetDbConnection(), (NpgsqlTransaction)transaction);

    private void ValidateScopeAndKey(string logicalHash)
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty)
            throw new InvalidOperationException(InvalidScope);
        _ = CacheKeyPolicy.PhysicalKey(logicalHash, Guid.Empty);
    }

    private void ValidateDatabaseScope(NpgsqlDataReader reader, int organizationOrdinal, int actorOrdinal)
    {
        if (reader.IsDBNull(organizationOrdinal) || reader.IsDBNull(actorOrdinal)
            || !Guid.TryParse(reader.GetString(organizationOrdinal), out Guid actualOrganization)
            || !Guid.TryParse(reader.GetString(actorOrdinal), out Guid actualActor)
            || actualOrganization != organization.OrganizationId || actualActor != organization.ActorId)
            throw new InvalidOperationException(InvalidScope);
    }
}
