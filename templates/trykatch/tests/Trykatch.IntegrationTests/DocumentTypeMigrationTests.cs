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
