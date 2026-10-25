using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private const string DocumentsEndpoint = "/api/v1/documents";
    private static readonly string[] DocumentsManageOnlyPermissions = ["documents.manage"];
    private static readonly string[] DocumentsReadOnlyPermissions = ["documents.read"];
    private static readonly byte[] FirstPdf = "%PDF-1.7 first payload"u8.ToArray();
    private static readonly byte[] SecondPdf = "%PDF-1.7 other payload"u8.ToArray();

    // SQL literals below split the stable preview event name so template renaming preserves the wire contract.

    [TestMethod]
    public async Task UploadRetryReturnsCurrentLifecycleDtoAndRecordsEffectsOnce()
    {
        await using UploadHost uploadHost = await UploadHost.StartAsync();
        using HttpClient administrator = await uploadHost.Host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await uploadHost.Host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid actorId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/auth/session")).GetProperty("userId").GetGuid();
        Guid operationId = Guid.NewGuid();

        JsonElement created = await UploadAsync(owner, operationId, FirstPdf, "Lifecycle document", "Created description");
        created.GetProperty("metadata").GetProperty("sizeBytes").GetInt64().ShouldBe(FirstPdf.Length);
        JsonElement retried = await UploadAsync(owner, operationId, FirstPdf, "Lifecycle document", "Created description");
        retried.GetRawText().ShouldBe(created.GetRawText());
        Guid documentId = created.GetProperty("id").GetGuid();

        JsonElement edited = await SendJsonAsync(owner, HttpMethod.Put, $"{DocumentsEndpoint}/{documentId}", new
        {
            title = "Edited document", description = "Edited description", documentType = "report"
        });
        edited.GetProperty("title").GetString().ShouldBe("Edited document");
        await SendNoContentAsync(owner, HttpMethod.Post, $"{DocumentsEndpoint}/{documentId}/archive");
        JsonElement archivedRetry = await UploadAsync(owner, operationId, FirstPdf, "Lifecycle document", "Created description");
        archivedRetry.GetProperty("id").GetGuid().ShouldBe(documentId);
        archivedRetry.GetProperty("title").GetString().ShouldBe("Edited document");
        archivedRetry.GetProperty("lifecycle").GetProperty("status").GetString().ShouldBe("Archived");

        await SendJsonAsync(owner, HttpMethod.Delete, $"{DocumentsEndpoint}/{documentId}", new
            { reason = "Lifecycle acceptance deletion request" });
        using HttpResponseMessage pendingRetry = await UploadResponseAsync(owner, operationId, FirstPdf,
            "Lifecycle document", "Created description");
        pendingRetry.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using NpgsqlConnection connection = new(uploadHost.Host.OwnerConnection);
        await connection.OpenAsync();
        (await ScalarAsync(connection, "SELECT count(*) FROM app.documents WHERE \"OrganizationId\"=@organization AND \"UploadOperationId\"=@operation", ("organization", organizationId), ("operation", operationId))).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.module_upload_operations WHERE \"OrganizationId\"=@organization AND \"OperationId\"=@operation AND \"State\"=1", ("organization", organizationId), ("operation", operationId))).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.organization_quota_reservations WHERE \"OrganizationId\"=@organization AND \"OperationId\"=@operation AND \"State\"=1", ("organization", organizationId), ("operation", operationId))).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.organization_usage WHERE \"OrganizationId\"=@organization AND \"EffectId\"=@operation AND \"SourceId\"=@document AND \"Units\"=@units", ("organization", organizationId), ("operation", operationId), ("document", documentId), ("units", FirstPdf.Length))).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\"=@organization AND \"ActorId\"=@actor AND \"Action\"='document.uploaded' AND \"SubjectId\"=@document", ("organization", organizationId), ("actor", actorId), ("document", documentId.ToString()))).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT count(*) FROM platform.outbox_messages WHERE \"Type\"=('Try' || 'katch.Modules.Documents.DocumentChanged') AND \"Payload\"::jsonb->>'documentId'=@document AND \"Payload\"::jsonb->>'operation'='uploaded'", ("document", documentId.ToString()))).ShouldBe(1L);
        created.TryGetProperty("objectKey", out _).ShouldBeFalse();
    }

    [TestMethod]
    public async Task UploadRejectsInvalidOperationIdsAndLegacyOmissionGetsFreshIdentities()
    {
        await using UploadHost uploadHost = await UploadHost.StartAsync();
        using HttpClient administrator = await uploadHost.Host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await uploadHost.Host.CreateOrganizationOwnerAsync(administrator);

        foreach (string[] invalidValues in new[]
        {
            new[] { "not-a-uuid" }, new[] { Guid.Empty.ToString() }, new[] { "" },
            new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() }
        })
        {
            using HttpResponseMessage invalid = await UploadResponseAsync(owner, invalidValues, FirstPdf, "Invalid operation", "No effect");
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        JsonElement firstLegacy = await UploadAsync(owner, null, FirstPdf, "Legacy one", "No operation field");
        JsonElement secondLegacy = await UploadAsync(owner, null, FirstPdf, "Legacy two", "No operation field");
        firstLegacy.GetProperty("id").GetGuid().ShouldNotBe(secondLegacy.GetProperty("id").GetGuid());
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        await using NpgsqlConnection connection = new(uploadHost.Host.OwnerConnection);
        await connection.OpenAsync();
        (await ScalarAsync(connection, "SELECT count(*) FROM app.documents WHERE \"OrganizationId\"=@organization", ("organization", organizationId))).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.documents WHERE \"OrganizationId\"=@organization AND \"UploadOperationId\" IS NOT NULL", ("organization", organizationId))).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT count(DISTINCT \"UploadOperationId\") FROM app.documents WHERE \"OrganizationId\"=@organization", ("organization", organizationId))).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.documents WHERE \"OrganizationId\"=@organization AND \"UploadOperationId\"='00000000-0000-0000-0000-000000000000'::uuid", ("organization", organizationId))).ShouldBe(0L);
        (await ScalarAsync(connection, "SELECT count(*) FROM app.organization_usage WHERE \"OrganizationId\"=@organization", ("organization", organizationId))).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\"=@organization AND \"Action\"='document.uploaded'", ("organization", organizationId))).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT count(*) FROM platform.outbox_messages WHERE \"Type\"=('Try' || 'katch.Modules.Documents.DocumentChanged') AND \"Payload\"::jsonb->>'operation'='uploaded'")).ShouldBe(2L);
    }

    [TestMethod]
    public async Task SameOperationWithDifferentBytesOrMetadataConflictsAndKeepsOriginalDownload()
    {
        await using UploadHost uploadHost = await UploadHost.StartAsync();
        using HttpClient administrator = await uploadHost.Host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await uploadHost.Host.CreateOrganizationOwnerAsync(administrator);
        Guid operationId = Guid.NewGuid();
        FirstPdf.Length.ShouldBe(SecondPdf.Length);
        JsonElement created = await UploadAsync(owner, operationId, FirstPdf, "Original title", "Original description");
        Guid documentId = created.GetProperty("id").GetGuid();

        using HttpResponseMessage differentBytes = await UploadResponseAsync(owner, operationId, SecondPdf, "Original title", "Original description");
        differentBytes.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using HttpResponseMessage differentMetadata = await UploadResponseAsync(owner, operationId, FirstPdf, "Changed title", "Original description");
        differentMetadata.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using HttpResponseMessage download = await owner.GetAsync($"{DocumentsEndpoint}/{documentId}/content");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(FirstPdf);
        JsonElement current = (await owner.GetFromJsonAsync<JsonElement>(DocumentsEndpoint)).EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == documentId);
        current.GetProperty("title").GetString().ShouldBe("Original title");
    }

    [TestMethod]
    public async Task ManageOnlyPermissionCanUploadButActorMismatchConflictsAndRevokedAccessIsForbidden()
    {
        await using UploadHost uploadHost = await UploadHost.StartAsync();
        using HttpClient administrator = await uploadHost.Host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await uploadHost.Host.CreateOrganizationOwnerAsync(administrator);
        JsonElement role = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Documents uploader", description = "Manage without viewing", permissions = DocumentsManageOnlyPermissions
        });
        Guid roleId = role.GetProperty("id").GetGuid();
        using HttpClient manager = await uploadHost.Host.InviteAndActivateAsync(owner, "documents-uploader@trykatch.test");
        Guid managerMembership = (await AccessHost.OrganizationMemberAsync(owner, "documents-uploader@trykatch.test")).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{managerMembership}", new
        {
            membershipId = managerMembership, roleIds = new[] { roleId }, isActive = true
        });

        Guid operationId = Guid.NewGuid();
        const string managerTitle = "Manager upload";
        const string managerDescription = "Manage permission only";
        JsonElement created = await UploadAsync(manager, operationId, FirstPdf, managerTitle, managerDescription);
        created.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        created.TryGetProperty("objectKey", out _).ShouldBeFalse();
        JsonElement managerRetry = await UploadAsync(manager, operationId, FirstPdf, managerTitle, managerDescription);
        managerRetry.GetRawText().ShouldBe(created.GetRawText());
        using HttpResponseMessage otherActor = await UploadResponseAsync(owner, operationId, FirstPdf, managerTitle, managerDescription);
        otherActor.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/roles/{roleId}", new
        {
            id = roleId, name = "Documents uploader", description = "Viewing after upload access was revoked",
            permissions = DocumentsReadOnlyPermissions
        });
        using HttpResponseMessage revoked = await UploadResponseAsync(manager, operationId, FirstPdf, managerTitle, managerDescription);
        revoked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AuditFailureRollsBackDocumentLedgerAndOutboxAndReleasesUploadAdmission()
    {
        await using UploadHost uploadHost = await UploadHost.StartAsync();
        using HttpClient administrator = await uploadHost.Host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await uploadHost.Host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid operationId = Guid.NewGuid();

        await using (NpgsqlConnection connection = new(uploadHost.Host.OwnerConnection))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand trigger = new("""
                CREATE FUNCTION platform.reject_test_document_upload_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Action" = 'document.uploaded' THEN
                    RAISE EXCEPTION 'Injected document audit persistence failure';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER reject_test_document_upload_audit BEFORE INSERT ON platform.audit_entries
                  FOR EACH ROW EXECUTE FUNCTION platform.reject_test_document_upload_audit();
                """, connection);
            await trigger.ExecuteNonQueryAsync();
        }

        using HttpResponseMessage failed = await UploadResponseAsync(owner, operationId, FirstPdf, "Rollback document", "Must roll back");
        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        string objectKey;
        await using (NpgsqlConnection connection = new(uploadHost.Host.OwnerConnection))
        {
            await connection.OpenAsync();
            objectKey = await ScalarStringAsync(connection, "SELECT \"ObjectKey\" FROM app.module_upload_operations WHERE \"OrganizationId\"=@organization AND \"OperationId\"=@operation", ("organization", organizationId), ("operation", operationId));
        }
        ConditionalObjectStat sentinel = await uploadHost.Host.Services.GetRequiredService<IConditionalObjectStorage>().StatAsync(objectKey);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        sentinel.Identity!.OperationId.ShouldBe(operationId);
        sentinel.Identity.OriginalLength.ShouldBe(FirstPdf.Length);

        await using (NpgsqlConnection connection = new(uploadHost.Host.OwnerConnection))
        {
            await connection.OpenAsync();
            (await ScalarAsync(connection, "SELECT count(*) FROM app.documents WHERE \"OrganizationId\"=@organization", ("organization", organizationId))).ShouldBe(0L);
            (await ScalarAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\"=@organization AND \"Action\"='document.uploaded'", ("organization", organizationId))).ShouldBe(0L);
            (await ScalarAsync(connection, "SELECT count(*) FROM platform.outbox_messages WHERE \"Type\"=('Try' || 'katch.Modules.Documents.DocumentChanged') AND \"Payload\"::jsonb->>'operation'='uploaded'")).ShouldBe(0L);
            (await ScalarAsync(connection, "SELECT count(*) FROM app.organization_usage WHERE \"OrganizationId\"=@organization", ("organization", organizationId))).ShouldBe(0L);
            (await ScalarAsync(connection, "SELECT count(*) FROM app.module_upload_operations WHERE \"OrganizationId\"=@organization AND \"OperationId\"=@operation AND \"State\"=3", ("organization", organizationId), ("operation", operationId))).ShouldBe(1L);
            (await ScalarAsync(connection, "SELECT count(*) FROM app.organization_quota_reservations WHERE \"OrganizationId\"=@organization AND \"OperationId\"=@operation AND \"State\"=2", ("organization", organizationId), ("operation", operationId))).ShouldBe(1L);
        }
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, Guid? operationId, byte[] bytes, string title,
        string description)
    {
        using HttpResponseMessage response = await UploadResponseAsync(client, operationId, bytes, title, description);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static Task<HttpResponseMessage> UploadResponseAsync(HttpClient client, Guid? operationId, byte[] bytes,
        string title, string description) => UploadResponseAsync(client,
            operationId is null ? Array.Empty<string>() : [operationId.Value.ToString()], bytes, title, description);

    private static async Task<HttpResponseMessage> UploadResponseAsync(HttpClient client, string[] operationIds,
        byte[] bytes, string title, string description)
    {
        string csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery")).GetProperty("token").GetString()!;
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(title), "title");
        multipart.Add(new StringContent(description), "description");
        multipart.Add(new StringContent("other"), "documentType");
        foreach (string operationId in operationIds) multipart.Add(new StringContent(operationId), "operationId");
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new("application/pdf");
        multipart.Add(file, "file", "acceptance.pdf");
        using HttpRequestMessage request = new(HttpMethod.Post, DocumentsEndpoint) { Content = multipart };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        string csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery")).GetProperty("token").GetString()!;
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return response.StatusCode == HttpStatusCode.NoContent ? default : (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task SendNoContentAsync(HttpClient client, HttpMethod method, string path)
    {
        string csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery")).GetProperty("token").GetString()!;
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class UploadHost(AccessHost host, string storageRoot) : IAsyncDisposable
    {
        public AccessHost Host { get; } = host;

        public static async Task<UploadHost> StartAsync()
        {
            string storageRoot = Path.Combine(Path.GetTempPath(), $"trykatch-document-http-{Guid.NewGuid():N}");
            AccessHost host = await AccessHost.StartAsync(additionalSettings: new Dictionary<string, string?>
            {
                ["Storage:LocalPath"] = storageRoot
            });
            return new(host, storageRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }
}
