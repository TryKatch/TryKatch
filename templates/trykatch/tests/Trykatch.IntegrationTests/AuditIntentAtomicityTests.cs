using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Security;
using Trykatch.Application;
using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class AuditIntentAtomicityTests
{
    private const string PostgresImage = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";

    [TestMethod]
    public async Task ProductionAdministrationHostStartsAndResolvesServicesWithoutDatabaseAccess()
    {
        const string unavailableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";
        await using AuditDatabase database = new(
            null, unavailableDatabase, unavailableDatabase, unavailableDatabase, unavailableDatabase);
        using IHost host = await database.StartAdministrationHostAsync(
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<OrganizationAdministration>().ShouldNotBeNull();
    }

    [TestMethod]
    public async Task ProductionAdministrationPipelineCommitsBusinessStateAndMatchingIntentBeforeSuccess()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        AdministrationActor actor = await database.SeedAdministrationActorAsync();
        using IHost host = await database.StartAdministrationHostAsync(actor);
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/test/roles"),
            HttpCompletionOption.ResponseHeadersRead);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.GetValues("X-Atomic-Success").ShouldBe(["true"]);
        Guid roleId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        Guid eventId = await database.FindIntentIdAsync(roleId);
        (await CountAsync(database.OwnerConnection, "platform.roles", roleId)).ShouldBe(1);
        (await CountAsync(database.OwnerConnection, "platform.audit_intents", eventId)).ShouldBe(1);

        await database.InstallProjectionFailureAsync(eventId);
        await using (AuditProjectionDbContext failingContext = new(
            new DbContextOptionsBuilder<AuditProjectionDbContext>().UseNpgsql(database.OutboxConnection).Options))
        {
            PostgresAuditIntentProjectionStore failingStore = new(failingContext);
            AuditIntent pending = (await failingStore.ReadPendingAsync(10, CancellationToken.None)).Single();
            PostgresException failure = await Should.ThrowAsync<PostgresException>(
                () => failingStore.ProjectAsync(pending, CancellationToken.None));
            failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        await database.RemoveProjectionFailureAsync();
        await using (AuditProjectionDbContext retryContext = new(
            new DbContextOptionsBuilder<AuditProjectionDbContext>().UseNpgsql(database.OutboxConnection).Options))
        {
            PostgresAuditIntentProjectionStore retryStore = new(retryContext);
            AuditIntent pending = (await retryStore.ReadPendingAsync(10, CancellationToken.None)).Single();
            (await retryStore.ProjectAsync(pending, CancellationToken.None)).ShouldBeTrue();
        }
        await using (AuditProjectionDbContext replayContext = new(
            new DbContextOptionsBuilder<AuditProjectionDbContext>().UseNpgsql(database.OutboxConnection).Options))
        {
            (await new PostgresAuditIntentProjectionStore(replayContext)
                .ReadPendingAsync(10, CancellationToken.None)).ShouldBeEmpty();
        }
        AuditProjectionIdentity projected = await database.ReadProjectedIdentityAsync(eventId);
        projected.ShouldBe(new(eventId, actor.OrganizationId, actor.ActorId));
    }

    [TestMethod]
    public async Task OrganizationRoleCannotMutateOrForgeAuditIntents()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        AdministrationActor actor = await database.SeedAdministrationActorAsync();
        using IHost host = await database.StartAdministrationHostAsync(actor);
        using HttpClient client = host.GetTestClient();
        using HttpResponseMessage response = await client.PostAsync("/test/roles", content: null);
        Guid roleId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Guid eventId = await database.FindIntentIdAsync(roleId);
        foreach (string sql in new[]
                 {
                     "UPDATE platform.audit_intents SET \"Operation\" = 'forged' WHERE \"Id\" = @event",
                     "DELETE FROM platform.audit_intents WHERE \"Id\" = @event"
                 })
            await AssertOrganizationStatementDeniedAsync(
                database.OrganizationConnection, actor, sql, ("event", eventId));
        await AssertCrossScopeIntentDeniedAsync(
            database.OrganizationConnection, actor, Guid.CreateVersion7(), actor.ActorId);
        await AssertCrossScopeIntentDeniedAsync(
            database.OrganizationConnection, actor, actor.OrganizationId, Guid.CreateVersion7());
    }

    private static async Task AssertOrganizationStatementDeniedAsync(
        string connectionString,
        AdministrationActor actor,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, actor.OrganizationId, actor.ActorId);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(connection, transaction, sql, parameters));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task AssertCrossScopeIntentDeniedAsync(
        string connectionString,
        AdministrationActor actor,
        Guid attemptedOrganizationId,
        Guid attemptedActorId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, actor.OrganizationId, actor.ActorId);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertIntentAsync(
            connection, transaction, Guid.CreateVersion7(), attemptedOrganizationId, attemptedActorId,
            Guid.CreateVersion7()));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [TestMethod]
    [DataRow("business")]
    [DataRow("intent")]
    [DataRow("commit")]
    [DataRow("disconnect")]
    public async Task ProductionAdministrationPipelinePublishesNoSuccessAndLeavesNoResidueOnFailure(string failure)
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        AdministrationActor actor = await database.SeedAdministrationActorAsync();
        await database.InstallFailureAsync(failure);
        using IHost host = await database.StartAdministrationHostAsync(actor);
        using HttpClient client = host.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/test/roles");
        request.Headers.Add("X-Test-Failure", failure);

        if (failure == "disconnect")
        {
            Exception disconnect = await Should.ThrowAsync<Exception>(() =>
                client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead));
            (disconnect is HttpRequestException or OperationCanceledException).ShouldBeTrue(
                $"Expected a transport/cancellation failure, received {disconnect.GetType().FullName}.");
        }
        else
        {
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            response.Headers.GetValues("X-Test-SqlState").Single().ShouldBe(
                failure == "commit" ? PostgresErrorCodes.ForeignKeyViolation : PostgresErrorCodes.CheckViolation);
            response.Headers.Contains("X-Atomic-Success").ShouldBeFalse();
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("Atomic pipeline role");
        }

        database.WasFailureReached(failure).ShouldBeTrue($"The injected {failure} seam was not reached.");
        (await database.CountRolesNamedAsync("Atomic pipeline role")).ShouldBe(0);
        (await database.CountIntentsNamedAsync("Atomic pipeline role")).ShouldBe(0);
    }

    [TestMethod]
    public async Task IntentFailureRollsBackBusinessStateAndProducesNoAudit()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        Guid organizationId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid roleId = Guid.CreateVersion7();
        Guid eventId = Guid.CreateVersion7();

        await using NpgsqlConnection organization = new(database.OrganizationConnection);
        await organization.OpenAsync();
        await using NpgsqlTransaction transaction = await organization.BeginTransactionAsync();
        await SetScopeAsync(organization, transaction, organizationId, actorId);
        await ExecuteAsync(organization, transaction, """
            INSERT INTO platform.roles ("Id", "OrganizationId", "Name", "Description", "IsSystem")
            VALUES (@role, @organization, 'Atomic role', '', false)
            """, ("role", roleId), ("organization", organizationId));
        PostgresException failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(organization, transaction, """
            INSERT INTO platform.audit_intents
                ("Id", "OrganizationId", "ActorId", "Operation", "SubjectType", "SubjectId", "SubjectDisplayName", "Details", "OccurredAt")
            VALUES (@event, @organization, @actor, 'role.created', 'Role', @subject, 'Atomic role',
                '{"token":"secret-sentinel"}'::jsonb, now())
            """, ("event", eventId), ("organization", organizationId), ("actor", actorId), ("subject", roleId.ToString())));
        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await transaction.RollbackAsync();

        (await CountAsync(database.OwnerConnection, "platform.roles", roleId)).ShouldBe(0);
        (await CountAsync(database.OwnerConnection, "platform.audit_intents", eventId)).ShouldBe(0);
        (await CountAsync(database.OwnerConnection, "platform.audit_entries", eventId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task BusinessFailureRollsBackIntentAndProducesNoAudit()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        Guid organizationId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid roleId = Guid.CreateVersion7();
        Guid eventId = Guid.CreateVersion7();
        await InsertRoleAsync(database.OwnerConnection, roleId, organizationId, "Existing role");

        await using NpgsqlConnection organization = new(database.OrganizationConnection);
        await organization.OpenAsync();
        await using NpgsqlTransaction transaction = await organization.BeginTransactionAsync();
        await SetScopeAsync(organization, transaction, organizationId, actorId);
        await InsertIntentAsync(organization, transaction, eventId, organizationId, actorId, roleId);
        PostgresException failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(organization, transaction, """
            INSERT INTO platform.roles ("Id", "OrganizationId", "Name", "Description", "IsSystem")
            VALUES (@role, @organization, 'Existing role', '', false)
            """, ("role", Guid.CreateVersion7()), ("organization", organizationId)));
        failure.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        await transaction.RollbackAsync();

        (await CountAsync(database.OwnerConnection, "platform.audit_intents", eventId)).ShouldBe(0);
        (await CountAsync(database.OwnerConnection, "platform.audit_entries", eventId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task CommittedIntentProjectsExactlyOnceUnderTenantRuntimeRoles()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        Guid organizationId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid roleId = Guid.CreateVersion7();
        Guid eventId = Guid.CreateVersion7();

        await using (NpgsqlConnection organization = new(database.OrganizationConnection))
        {
            await organization.OpenAsync();
            await using NpgsqlTransaction transaction = await organization.BeginTransactionAsync();
            await SetScopeAsync(organization, transaction, organizationId, actorId);
            await ExecuteAsync(organization, transaction, """
                INSERT INTO platform.roles ("Id", "OrganizationId", "Name", "Description", "IsSystem")
                VALUES (@role, @organization, 'Projected role', '', false)
                """, ("role", roleId), ("organization", organizationId));
            await InsertIntentAsync(organization, transaction, eventId, organizationId, actorId, roleId);
            await transaction.CommitAsync();
        }

        await using AuditProjectionDbContext projection = new(
            new DbContextOptionsBuilder<AuditProjectionDbContext>().UseNpgsql(database.OutboxConnection).Options);
        PostgresAuditIntentProjectionStore store = new(projection);
        AuditIntent intent = (await store.ReadPendingAsync(10, CancellationToken.None)).Single();
        (await store.ProjectAsync(intent, CancellationToken.None)).ShouldBeTrue();
        (await store.ProjectAsync(intent, CancellationToken.None)).ShouldBeFalse();
        (await store.ReadPendingAsync(10, CancellationToken.None)).ShouldBeEmpty();

        (await CountAsync(database.OwnerConnection, "platform.roles", roleId)).ShouldBe(1);
        (await CountAsync(database.OwnerConnection, "platform.audit_intents", eventId)).ShouldBe(1);
        (await CountAsync(database.OwnerConnection, "platform.audit_entries", eventId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task CommitFailureRollsBackBothSameRoleContexts()
    {
        await using AuditDatabase database = await AuditDatabase.StartAsync();
        Guid organizationId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Role role = Role.Create(organizationId, "Shared transaction role");
        OutboxMessage message = new() { Type = "atomicity.probe", Payload = "{}" };
        await CreateDeferredFailureProbeAsync(database.OwnerConnection);

        await using OrganizationControlPlaneDbContext control = new(
            new DbContextOptionsBuilder<OrganizationControlPlaneDbContext>().UseNpgsql(database.OrganizationConnection).Options);
        await using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(database.OrganizationConnection).Options);
        await using var transaction = await control.Database.BeginTransactionAsync();
        await SetScopeAsync((NpgsqlConnection)control.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction(), organizationId, actorId);
        await using var enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
            control, application, organizationId, actorId, CancellationToken.None);
        control.Roles.Add(role);
        application.OutboxMessages.Add(message);
        await control.SaveChangesAsync();
        await application.SaveChangesAsync();
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.test_deferred_commit ("Id", "ParentId")
            VALUES ({Guid.CreateVersion7()}, {Guid.CreateVersion7()})
            """);

        PostgresException failure = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
        failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        (await CountAsync(database.OwnerConnection, "platform.roles", role.Id)).ShouldBe(0);
        (await CountAsync(database.OwnerConnection, "platform.outbox_messages", message.Id)).ShouldBe(0);
    }

    private static async Task InsertRoleAsync(string connectionString, Guid roleId, Guid organizationId, string name)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, """
            INSERT INTO platform.roles ("Id", "OrganizationId", "Name", "Description", "IsSystem")
            VALUES (@role, @organization, @name, '', false)
            """, ("role", roleId), ("organization", organizationId), ("name", name));
    }

    private static async Task CreateDeferredFailureProbeAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"""
            CREATE TABLE platform.test_deferred_commit (
                "Id" uuid PRIMARY KEY,
                "ParentId" uuid NOT NULL,
                CONSTRAINT fk_test_deferred_commit
                  FOREIGN KEY ("ParentId") REFERENCES platform.test_deferred_commit ("Id")
                  DEFERRABLE INITIALLY DEFERRED);
            GRANT INSERT ON platform.test_deferred_commit TO {PostgresRuntimeRoleFixture.OrganizationRole};
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task InsertIntentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid eventId, Guid organizationId, Guid actorId, Guid roleId) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO platform.audit_intents
                ("Id", "OrganizationId", "ActorId", "Operation", "SubjectType", "SubjectId", "SubjectDisplayName", "Details", "OccurredAt")
            VALUES (@event, @organization, @actor, 'role.created', 'Role', @subject, 'Projected role',
                '{"permissionCount":"1"}'::jsonb, now())
            """, ("event", eventId), ("organization", organizationId), ("actor", actorId), ("subject", roleId.ToString()));

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organizationId, Guid actorId) =>
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)",
            ("organization", organizationId.ToString()), ("actor", actorId.ToString()));

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString, string relation, Guid roleId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"SELECT count(*) FROM {relation} WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", roleId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record AdministrationActor(Guid OrganizationId, Guid ActorId, Guid MembershipId);
    private sealed record AuditProjectionIdentity(Guid EventId, Guid OrganizationId, Guid ActorId);

    private sealed class AuditDatabase(
        PostgreSqlContainer? postgres,
        string ownerConnection,
        string organizationConnection,
        string platformConnection,
        string outboxConnection) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, byte> reachedFailures = new(StringComparer.Ordinal);
        public string OwnerConnection => ownerConnection;
        public string OrganizationConnection => organizationConnection;
        public string PlatformConnection => platformConnection;
        public string OutboxConnection => outboxConnection;

        public async Task<AdministrationActor> SeedAdministrationActorAsync()
        {
            Organization organization = Organization.Create("Atomic organization", $"atomic-{Guid.NewGuid():N}"[..30]);
            Guid actorId = Guid.CreateVersion7();
            Role owner = Role.Create(organization.Id, "Owner", "Protected owner", isSystem: true);
            owner.SetPermissions([Permissions.RolesManage]);
            Membership membership = Membership.Create(organization.Id, actorId);
            membership.AssignRole(owner.Id);
            await using PlatformDbContext context = new(
                new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ownerConnection).Options);
            context.AddRange(organization, owner, membership);
            await context.SaveChangesAsync();
            return new(organization.Id, actorId, membership.Id);
        }

        public async Task<IHost> StartAdministrationHostAsync(AdministrationActor actor)
        {
            Dictionary<string, string?> settings = new()
            {
                ["ConnectionStrings:trykatch-organization"] = organizationConnection,
                ["ConnectionStrings:trykatch-platform"] = platformConnection,
                ["ConnectionStrings:trykatch-outbox"] = outboxConnection,
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = "1",
                ["Email:From"] = "atomicity@example.test",
                ["Email:Security"] = "StartTls"
            };
            IHost host = await Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
                .ConfigureWebHost(webHost => webHost.UseTestServer()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddRouting();
                        services.AddApplication();
                        services.AddModules(context.Configuration, [new ProjectsModule(), new DocumentsModule()]);
                        services.AddInfrastructure(context.Configuration);
                        foreach (ServiceDescriptor hostedService in services.Where(descriptor =>
                                     descriptor.ServiceType == typeof(IHostedService)
                                     && descriptor.ImplementationType is Type implementation
                                     && (implementation == typeof(AuditIntentProjectionWorker)
                                         || implementation == typeof(OutboxProcessor))).ToArray())
                            services.Remove(hostedService);
                        services.AddSingleton<IUserDirectory, UnusedUserDirectory>();
                        services.AddSingleton<IWorkspaceContextCookie, UnusedWorkspaceCookie>();
                        services.AddSingleton<IOptions<AtomicMutationResponseOptions>>(
                            Options.Create(new AtomicMutationResponseOptions { MaximumBytes = 4096 }));
                    })
                    .Configure(app =>
                    {
                        app.UseExceptionHandler(handler => handler.Run(async context =>
                        {
                            Exception? error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                            while (error is not null && error is not PostgresException) error = error.InnerException;
                            if (error is PostgresException postgres)
                                context.Response.Headers.Append("X-Test-SqlState", postgres.SqlState);
                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            await context.Response.WriteAsync("atomic failure");
                        }));
                        app.UseRouting();
                        app.Use(async (context, next) =>
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, actor.ActorId.ToString()),
                                new Claim("organization_id", actor.OrganizationId.ToString())
                            ], "test"));
                            context.RequestServices.GetRequiredService<IOrganizationContextInitializer>().Initialize(
                                new OrganizationAccess(actor.OrganizationId, "atomic", actor.ActorId, actor.MembershipId,
                                    new HashSet<string> { Permissions.RolesManage }));
                            await next();
                        });
                        app.UseMiddleware<PlatformDataTransactionMiddleware>();
                        app.UseMiddleware<OrganizationTransactionMiddleware>();
                        app.UseEndpoints(endpoints => endpoints.MapPost("/test/roles", async context =>
                        {
                            OrganizationAdministration administration = context.RequestServices.GetRequiredService<OrganizationAdministration>();
                            string failure = context.Request.Headers["X-Test-Failure"].ToString();
                            if (!string.IsNullOrEmpty(failure)) reachedFailures.TryAdd(failure, 0);
                            Result<RoleDto> result = await administration.SaveRoleAsync(
                                new SaveRoleCommand(null, "Atomic pipeline role", "", []), context.RequestAborted);
                            result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
                            if (failure == "commit")
                            {
                                OrganizationControlPlaneDbContext control = context.RequestServices.GetRequiredService<OrganizationControlPlaneDbContext>();
                                await control.Database.ExecuteSqlInterpolatedAsync($"""
                                    INSERT INTO platform.test_deferred_commit ("Id", "ParentId")
                                    VALUES ({Guid.CreateVersion7()}, {Guid.CreateVersion7()})
                                    """, context.RequestAborted);
                            }
                            if (failure == "disconnect") context.Abort();
                            context.Response.StatusCode = StatusCodes.Status201Created;
                            context.Response.Headers.Append("X-Atomic-Success", "true");
                            await context.Response.StartAsync();
                            await context.Response.WriteAsJsonAsync(new { id = result.Value!.Id, name = result.Value.Name }, CancellationToken.None);
                        }).WithMetadata(new OrganizationScopedAttribute()));
                    }))
                .StartAsync();
            return host;
        }

        public bool WasFailureReached(string failure) => reachedFailures.ContainsKey(failure);

        public async Task InstallFailureAsync(string failure)
        {
            string sql = failure switch
            {
                "business" => "ALTER TABLE platform.roles ADD CONSTRAINT test_business_failure CHECK (\"Name\" <> 'Atomic pipeline role')",
                "intent" => "ALTER TABLE platform.audit_intents ADD CONSTRAINT test_intent_failure CHECK (\"SubjectDisplayName\" <> 'Atomic pipeline role')",
                "commit" => """
                    CREATE TABLE platform.test_deferred_commit (
                      "Id" uuid PRIMARY KEY,
                      "ParentId" uuid NOT NULL,
                      CONSTRAINT fk_test_deferred_commit FOREIGN KEY ("ParentId")
                        REFERENCES platform.test_deferred_commit ("Id") DEFERRABLE INITIALLY DEFERRED);
                    GRANT INSERT ON platform.test_deferred_commit TO trykatch_org_runtime;
                    """,
                "disconnect" => "SELECT 1",
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await ExecuteAsync(connection, null, sql);
        }

        public async Task InstallProjectionFailureAsync(Guid eventId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                $"ALTER TABLE platform.audit_entries ADD CONSTRAINT test_projection_failure CHECK (\"Id\" <> '{eventId}'::uuid)");
        }

        public async Task RemoveProjectionFailureAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "ALTER TABLE platform.audit_entries DROP CONSTRAINT test_projection_failure");
        }

        public async Task<AuditProjectionIdentity> ReadProjectedIdentityAsync(Guid eventId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                "SELECT \"Id\", \"OrganizationId\", \"ActorId\" FROM platform.audit_entries WHERE \"Id\" = @event", connection);
            command.Parameters.AddWithValue("event", eventId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
        }

        public Task<long> CountRolesNamedAsync(string name) => CountNamedAsync("platform.roles", "Name", name);
        public Task<long> CountIntentsNamedAsync(string name) => CountNamedAsync("platform.audit_intents", "SubjectDisplayName", name);

        public async Task<Guid> FindIntentIdAsync(Guid subjectId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                "SELECT \"Id\" FROM platform.audit_intents WHERE \"SubjectId\" = @subject", connection);
            command.Parameters.AddWithValue("subject", subjectId.ToString());
            return (Guid)(await command.ExecuteScalarAsync())!;
        }

        private async Task<long> CountNamedAsync(string relation, string column, string value)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new($"SELECT count(*) FROM {relation} WHERE \"{column}\" = @value", connection);
            command.Parameters.AddWithValue("value", value);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public static async Task<AuditDatabase> StartAsync()
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(PostgresImage).Build();
            await postgres.StartAsync();
            string owner = postgres.GetConnectionString();
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
            await using IdentityDbContext identity = new(
                new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options);
            await identity.Database.MigrateAsync();
            await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
            await platform.Database.MigrateAsync();
            ProjectsModule projects = new();
            DocumentsModule documents = new();
            ModuleCatalog catalog = new([projects, documents]);
            await using ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                [new ProjectsModelContributor(), new DocumentsModelContributor()],
                moduleCatalog: catalog);
            await application.Database.MigrateAsync();
            await using (NpgsqlConnection moduleConnection = new(owner))
            {
                await moduleConnection.OpenAsync();
                foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(catalog.Modules, []))
                    await ExecuteAsync(moduleConnection, null, migration.Sql);
            }
            await InstalledSchemaCatalog.SynchronizeAsync(owner, catalog.Descriptors.SelectMany(module =>
                module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
            await PostgresRuntimeRoleFixture.GrantApplicationPrivilegesAsync(owner);
            (string organization, string platformRuntime, _, string outbox) = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
            return new(postgres, owner, organization, platformRuntime, outbox);
        }

        public ValueTask DisposeAsync() => postgres?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class UnusedUserDirectory : IUserDirectory
    {
        public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, UserSummary>> GetUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedWorkspaceCookie : IWorkspaceContextCookie
    {
        public bool TryRead(HttpContext context, out Guid organizationId) { organizationId = Guid.Empty; return false; }
        public void Write(HttpContext context, Guid organizationId, bool persistent) => throw new NotSupportedException();
        public void Clear(HttpContext context) => throw new NotSupportedException();
    }
}
