using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class DocumentTypeMigrationTests
{
    [TestMethod]
    public async Task UploadOperationMigrationPreservesHistoricalFilesAndLifecycleWithoutInventingOperations()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await using NpgsqlConnection connection = new(postgres.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE SCHEMA app;");
        DocumentsModule module = new();
        const string migrationId = "202610242300_upload_operation_identity";
        foreach (ModuleMigration migration in module.Migrations.TakeWhile(migration => migration.Id != migrationId))
            await ExecuteAsync(connection, migration.Sql);
        await ExecuteAsync(connection, """
            INSERT INTO app.documents
              ("Id", "OrganizationId", "CreatedBy", "Title", "Content", "CreatedAt", "ObjectKey", "SizeBytes", "ArchivedAt", "DeletedAt")
            SELECT ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid,
              '10000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001',
              'Retained', '', CURRENT_TIMESTAMP, 'retained/' || n, 42,
              CASE WHEN n > 1 THEN CURRENT_TIMESTAMP ELSE NULL END,
              CASE WHEN n = 3 THEN CURRENT_TIMESTAMP ELSE NULL END
            FROM generate_series(1, 3) AS n;
            """);
        await ExecuteAsync(connection, module.Migrations.Single(migration => migration.Id == migrationId).Sql);
        await using NpgsqlCommand read = new("""
            SELECT "UploadOperationId", "ObjectKey", "SizeBytes", "ArchivedAt" IS NOT NULL, "DeletedAt" IS NOT NULL,
              c.relrowsecurity, c.relforcerowsecurity
            FROM app.documents CROSS JOIN pg_class c WHERE c.oid = 'app.documents'::regclass ORDER BY "Id";
            """, connection);
        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();
        for (int row = 1; row <= 3; row++)
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.IsDBNull(0).ShouldBeTrue();
            reader.GetString(1).ShouldBe($"retained/{row}");
            reader.GetInt64(2).ShouldBe(42);
            reader.GetBoolean(3).ShouldBe(row > 1);
            reader.GetBoolean(4).ShouldBe(row == 3);
            reader.GetBoolean(5).ShouldBeTrue();
            reader.GetBoolean(6).ShouldBeTrue();
        }
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    [TestMethod]
    public async Task ExistingDocumentsReceiveOtherWithoutChangingFileMetadataOrRls()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await using NpgsqlConnection connection = new(postgres.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE SCHEMA app;");
        DocumentsModule module = new();
        foreach (ModuleMigration migration in module.Migrations.TakeWhile(migration => migration.Id != "202609151400_document_type"))
            await ExecuteAsync(connection, migration.Sql);
        await ExecuteAsync(connection, """
            INSERT INTO app.documents ("Id", "OrganizationId", "CreatedBy", "Title", "Content", "CreatedAt", "ObjectKey", "FileName")
            VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002',
                    '00000000-0000-0000-0000-000000000003', 'Existing', '', now(), 'opaque/retained-key', 'existing.pdf');
            """);
        await ExecuteAsync(connection, module.Migrations.Single(migration => migration.Id == "202609151400_document_type").Sql);
        await using NpgsqlCommand read = new("""
            SELECT d."DocumentType", d."ObjectKey", d."FileName", c.relrowsecurity, c.relforcerowsecurity
            FROM app.documents d CROSS JOIN pg_class c WHERE c.oid = 'app.documents'::regclass;
            """, connection);
        await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe("other");
            reader.GetString(1).ShouldBe("opaque/retained-key");
            reader.GetString(2).ShouldBe("existing.pdf");
            reader.GetBoolean(3).ShouldBeTrue();
            reader.GetBoolean(4).ShouldBeTrue();
        }
        PostgresException exception = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(connection, "UPDATE app.documents SET \"DocumentType\" = 'unknown';"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
