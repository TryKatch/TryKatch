using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Persistence.Migrations.Platform;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresIsolationInspectionTests
{
    [TestMethod]
    public async Task CleanFixtureSatisfiesExactHostPoliciesAndOrganizationPrivileges()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, inspection.Errors));
    }

    [TestMethod]
    [DataRow("projects", "projects", "Horizon.Modules.Projects.Domain.Project", "Horizon.Domain.Projects.Project")]
    [DataRow("projects", "projects", "Northwind.Crm.Modules.Projects.Domain.Project", "Northwind.Crm.Domain.Projects.Project")]
    [DataRow("documents", "documents", "Horizon.Modules.Documents.Domain.DocumentRecord", "Try" + "katch.Modules.Documents.DocumentRecord")]
    public void PreviewNineEntityTypeNamesAreDerivedForGeneratedApplications(
        string moduleId, string table, string currentEntityType, string expectedLegacyEntityType)
    {
        InstalledDataResource resource = new(moduleId,
            new(table, "app", table, ModuleDataOwnership.Organization, currentEntityType, table + "_organization_isolation"));

        InstalledSchemaCatalog.LegacyEntityTypeFor(resource).ShouldBe(expectedLegacyEntityType);
    }

    [TestMethod]
    public async Task PreviewNineModuleDeclarationsAreUpgradedToTheirMovedEntityTypes()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE app.documents (id integer)");
        InstalledDataResource[] current =
        [
            new("projects", new ProjectsModule().Descriptor.DataResources.Single()),
            new("documents", new DocumentsModule().Descriptor.DataResources.Single())
        ];
        InstalledDataResource[] previewNine =
        [
            current[0] with
            {
                Resource = current[0].Resource with
                {
                    EntityType = "TrykatchApp.Domain.Projects.Project"
                }
            },
            current[1] with
            {
                Resource = current[1].Resource with
                {
                    EntityType = "Try" + "katch.Modules.Documents.DocumentRecord"
                }
            }
        ];
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString, previewNine);

        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString, current);

        (await InstalledSchemaCatalog.ReadAsync(database.ConnectionString))
            .Select(resource => resource.EntityType)
            .ShouldBe(current.Select(item => item.Resource.EntityType), ignoreOrder: true);
    }

    [TestMethod]
    public async Task PreviewNineCompatibilityDoesNotAuthorizeOtherDeclarationChanges()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        InstalledDataResource current = new("projects", new ProjectsModule().Descriptor.DataResources.Single());
        InstalledDataResource changed = current with
        {
            Resource = current.Resource with
            {
                EntityType = "TrykatchApp.Domain.Projects.Project",
                IsolationPolicy = "unexpected_policy"
            }
        };
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString, [changed]);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString, [current]));

        exception.Message.ShouldContain("explicit reviewed data migration");
    }

    [TestMethod]
    [DataRow("command")]
    [DataRow("role")]
    public async Task RejectsPoliciesWithWrongCommandOrRole(string change)
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        string clause = change == "command" ? "FOR UPDATE" : $"TO {database.OwnerRole}";
        await database.ExecuteAsync($"""
            DROP POLICY projects_organization_isolation ON app.projects;
            CREATE POLICY projects_organization_isolation ON app.projects {clause}
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
            """);
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsColumnGrantsOnControlPlaneSecrets()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"CREATE TABLE platform.secrets (secret text); GRANT SELECT (secret) ON platform.secrets TO {database.RuntimeRole}");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsUndeclaredQuotedRelationsInEveryManagedSchema()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA identity; CREATE TABLE identity.\"UndeclaredQuoted\" (id uuid)");

        InvalidOperationException preflight = await Should.ThrowAsync<InvalidOperationException>(() =>
            InstalledSchemaCatalog.ValidateDeclaredObjectsAsync(database.ConnectionString,
                ["app.projects", "platform.audit_entries", "platform.outbox_messages"]));
        PostgresIsolationInspection inspection = await database.InspectAsync();

        preflight.Message.ShouldContain("Runtime grants were not applied");
        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("identity.UndeclaredQuoted", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsEffectivePrivilegesInheritedFromPublic()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("GRANT SELECT ON platform.outbox_messages TO PUBLIC");

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("forbidden SELECT", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsManagedSchemaFunctionsExecutableThroughPublic()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE FUNCTION app.unapproved() RETURNS integer LANGUAGE sql AS 'SELECT 1'");

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("function", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FourRuntimeProfilesRejectCrossRoleApplicationAccess()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        string suffix = Guid.NewGuid().ToString("N");
        string platform = $"platform_{suffix}";
        string identity = $"identity_{suffix}";
        string outbox = $"outbox_{suffix}";
        await database.ExecuteAsync($"""
            CREATE ROLE {platform} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {identity} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {outbox} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            GRANT USAGE ON SCHEMA platform TO {outbox};
            GRANT SELECT, UPDATE ON platform.outbox_messages TO {outbox};
            GRANT SELECT ON platform.audit_intents TO {outbox};
            GRANT SELECT, INSERT ON platform.audit_entries TO {outbox};
            """);
        try
        {
            RuntimeDatabaseRoles roles = new(database.RuntimeRole, platform, identity, outbox);
            (await PostgresIsolationInspector.InspectAsync(
                database.ConnectionString, roles, [new ProjectsModule().Descriptor])).IsValid.ShouldBeTrue();

            await database.ExecuteAsync($"GRANT USAGE ON SCHEMA app TO {platform}; GRANT SELECT ON app.projects TO {platform}");

            (await PostgresIsolationInspector.InspectAsync(
                database.ConnectionString, roles, [new ProjectsModule().Descriptor])).IsValid.ShouldBeFalse();
        }
        finally
        {
            await database.ExecuteAsync($"DROP OWNED BY {platform}, {identity}, {outbox}; DROP ROLE {platform}, {identity}, {outbox}");
        }
    }

    [TestMethod]
    public async Task DisabledModuleKeepsItsValidatedSchemaWithoutEnabledModuleCode()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString,
            [new("projects", new ProjectsModule().Descriptor.DataResources.Single())]);
        await database.ExecuteAsync($"GRANT SELECT ON platform.module_data_resources TO {database.RuntimeRole}");
        PostgresIsolationInspection retainedInspection =
            await PostgresIsolationInspector.InspectAsync(database.ConnectionString, database.RuntimeRole, []);
        retainedInspection.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, retainedInspection.Errors));
        await database.ExecuteAsync("CREATE POLICY unexpected_access ON app.projects USING (true) WITH CHECK (true)");
        (await PostgresIsolationInspector.InspectAsync(database.ConnectionString, database.RuntimeRole, [])).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task PlatformResourceDoesNotPermitOrganizationRuntimeAccess()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE platform.reports (id int)");
        ModuleDescriptor platform = new ProjectsModule().Descriptor with
        {
            Id = "reporting",
            DefaultDataOwnership = ModuleDataOwnership.Platform,
            DataResources = [new("reports", "platform", "reports", ModuleDataOwnership.Platform, AccessRule: ModuleDataAccessRule.PlatformOnly)]
        };
        ModuleDescriptor[] modules = [new ProjectsModule().Descriptor, platform];
        (await PostgresIsolationInspector.InspectAsync(database.ConnectionString, database.RuntimeRole, modules)).IsValid.ShouldBeTrue();
        await database.ExecuteAsync($"GRANT SELECT ON platform.reports TO {database.RuntimeRole}");
        (await PostgresIsolationInspector.InspectAsync(database.ConnectionString, database.RuntimeRole, modules)).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task OrganizationAuthorizationUsesTenantPoolAndRejectsCrossOrganizationRoleAttachment()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await using PlatformDbContext owner = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.ConnectionString).Options);
        await database.ExecuteAsync(owner.Database.GenerateCreateScript());
        foreach (SqlOperation operation in new ScopeControlPlaneAccess().UpOperations.OfType<SqlOperation>())
        {
            string testRoles = operation.Sql
                .Replace("trykatch_org_runtime", database.RuntimeRole, StringComparison.Ordinal)
                .Replace("trykatch_platform_runtime", database.OwnerRole, StringComparison.Ordinal);
            await database.ExecuteAsync(testRoles);
        }
        await database.ExecuteAsync($"""
            GRANT SELECT ON platform.organizations TO {database.RuntimeRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.roles, platform.memberships,
              platform.membership_roles, platform.role_permissions, platform.invitations TO {database.RuntimeRole};
            """);
        Guid actor = Guid.NewGuid();
        Organization organizationA = Organization.Create("Organization A", "organization-a");
        Organization organizationB = Organization.Create("Organization B", "organization-b");
        Role roleA = Role.Create(organizationA.Id, "Member");
        Role roleB = Role.Create(organizationB.Id, "Member");
        Membership membershipA = Membership.Create(organizationA.Id, actor);
        membershipA.AssignRole(roleA.Id);
        owner.AddRange(organizationA, organizationB, roleA, roleB, membershipA);
        await owner.SaveChangesAsync();

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatch-organization"] = database.RuntimeConnection,
            // This deliberately unusable connection proves tenant resolution never opens the platform pool.
            ["ConnectionStrings:trykatch-platform"] = "Host=invalid-platform-host;Database=forbidden;Username=forbidden;Timeout=1",
            ["ConnectionStrings:trykatch-outbox"] = database.RuntimeConnection
        }).Build();
        ServiceCollection services = new();
        services.AddApplication().AddInfrastructure(configuration);
        services.AddModules(configuration, [new ProjectsModule()]);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OrganizationControlPlaneDbContext tenant = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        await using var transaction = await tenant.Database.BeginTransactionAsync();
        IOrganizationAccessResolver resolver = scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>();
        (await resolver.ResolveAsync(actor, organizationA.Id)).ShouldNotBeNull();
        (await resolver.ResolveAsync(actor, organizationB.Id)).ShouldBeNull();
        await resolver.ResolveAsync(actor, organizationA.Id);
        tenant.Roles.Select(role => role.OrganizationId).Distinct().ToArray().ShouldBe([organizationA.Id]);
        await Should.ThrowAsync<PostgresException>(() => tenant.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO platform.membership_roles (\"MembershipId\", \"RoleId\") VALUES ({membershipA.Id}, {roleB.Id})"));
    }

    [TestMethod]
    public async Task RejectsAdditionalPermissivePolicyThatExposesAnotherOrganization()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        (await database.InspectAsync()).IsValid.ShouldBeTrue();
        await database.ExecuteAsync("CREATE POLICY unexpected_access ON app.projects USING (true) WITH CHECK (true)");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsOrganizationKeywordsInsideAnAlwaysTrueExpression()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            ALTER POLICY projects_organization_isolation ON app.projects
            USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid OR true)
            WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid OR true)
            """);
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsRuntimeRoleThatCanAssumeAnOwningRole()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"ALTER TABLE app.projects OWNER TO {database.OwnerRole}; GRANT {database.OwnerRole} TO {database.RuntimeRole}");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsRuntimeRoleThatCanAssumeABypassRole()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"ALTER ROLE {database.OwnerRole} BYPASSRLS; GRANT {database.OwnerRole} TO {database.RuntimeRole}");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsTruncateGrantThatBypassesRowSecurity()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"GRANT TRUNCATE ON app.projects TO {database.RuntimeRole}");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsGrantOptionOnOrganizationData()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"GRANT SELECT ON app.projects TO {database.RuntimeRole} WITH GRANT OPTION");
        (await database.InspectAsync()).IsValid.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RejectsEffectiveAccessToCustomSchemaTables()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"""
            CREATE SCHEMA custom_schema;
            CREATE TABLE custom_schema.secret (id integer PRIMARY KEY, value text NOT NULL);
            GRANT USAGE ON SCHEMA custom_schema TO {database.RuntimeRole};
            GRANT SELECT ON custom_schema.secret TO {database.RuntimeRole};
            """);

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("custom_schema.secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsCustomSecurityDefinerFunctions()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"""
            CREATE SCHEMA custom_schema;
            CREATE FUNCTION custom_schema.read_secret() RETURNS integer
              LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog
              AS 'SELECT 1';
            GRANT USAGE ON SCHEMA custom_schema TO {database.RuntimeRole};
            GRANT EXECUTE ON FUNCTION custom_schema.read_secret() TO {database.RuntimeRole};
            """);

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("custom_schema.read_secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsPrivilegedCatalogFunctionWhenCompatibilityFunctionIsAbsent()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync(
            $"GRANT EXECUTE ON FUNCTION pg_catalog.pg_read_file(text) TO {database.RuntimeRole}");

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("privileged function", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AcceptsOnlyTheExactHostOwnedLegacyOutboxRedactionFunction()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync($"""
            ALTER TABLE platform.outbox_messages
              ADD COLUMN "LastError" text NULL,
              ADD COLUMN "LastErrorCode" character varying(80) NULL,
              ADD COLUMN "LastErrorType" character varying(500) NULL;
            CREATE FUNCTION platform.redact_legacy_outbox_error()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
            AS $function$
            BEGIN
              IF NEW."LastError" IS NOT NULL THEN
                NEW."LastErrorCode" := COALESCE(NEW."LastErrorCode", 'legacy_unclassified');
                NEW."LastErrorType" := COALESCE(NEW."LastErrorType", 'legacy_exception');
                NEW."LastError" := NULL;
              END IF;
              RETURN NEW;
            END
            $function$;
            REVOKE ALL ON FUNCTION platform.redact_legacy_outbox_error() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION platform.redact_legacy_outbox_error() TO {database.RuntimeRole};
            CREATE TRIGGER redact_legacy_outbox_error
            BEFORE INSERT OR UPDATE OF "LastError" ON platform.outbox_messages
            FOR EACH ROW
            EXECUTE FUNCTION platform.redact_legacy_outbox_error();
            """);

        (await database.InspectAsync()).IsValid.ShouldBeTrue();

        await database.ExecuteAsync("ALTER TABLE platform.outbox_messages DISABLE TRIGGER redact_legacy_outbox_error");
        PostgresIsolationInspection disabled = await database.InspectAsync();
        disabled.IsValid.ShouldBeFalse();
        disabled.Errors.ShouldContain(error => error.Contains("differs from its approved", StringComparison.Ordinal));
        await database.ExecuteAsync("ALTER TABLE platform.outbox_messages ENABLE TRIGGER redact_legacy_outbox_error");

        await database.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION platform.redact_legacy_outbox_error()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
            AS 'BEGIN RETURN NEW; END';
            """);

        PostgresIsolationInspection tampered = await database.InspectAsync();
        tampered.IsValid.ShouldBeFalse();
        tampered.Errors.ShouldContain(error => error.Contains("differs from its approved", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsPublicSequencePrivilegesInCustomSchemas()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            CREATE SCHEMA custom_schema;
            CREATE SEQUENCE custom_schema.secret_sequence;
            GRANT USAGE ON SCHEMA custom_schema TO PUBLIC;
            GRANT USAGE ON SEQUENCE custom_schema.secret_sequence TO PUBLIC;
            """);

        PostgresIsolationInspection inspection = await database.InspectAsync();

        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("custom_schema.secret_sequence", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeceptivePermanentSchemaNamesCannotEscapeCatalogInspection()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("""
            CREATE SCHEMA pgxtempyescape;
            CREATE TABLE pgxtempyescape.secret (id integer PRIMARY KEY, value text NOT NULL);
            CREATE SEQUENCE pgxtempyescape.secret_sequence;
            CREATE FUNCTION pgxtempyescape.read_secret() RETURNS text
              LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog
              AS 'SELECT ''classified''::text';
            GRANT USAGE, CREATE ON SCHEMA pgxtempyescape TO PUBLIC;
            GRANT SELECT ON TABLE pgxtempyescape.secret TO PUBLIC;
            GRANT USAGE ON SEQUENCE pgxtempyescape.secret_sequence TO PUBLIC;
            GRANT EXECUTE ON FUNCTION pgxtempyescape.read_secret() TO PUBLIC;
            """);

        InvalidOperationException preflight = await Should.ThrowAsync<InvalidOperationException>(() =>
            InstalledSchemaCatalog.ValidateDeclaredObjectsAsync(database.ConnectionString,
                ["app.projects", "platform.audit_entries", "platform.outbox_messages"]));
        PostgresIsolationInspection inspection = await database.InspectAsync();

        preflight.Message.ShouldContain("pgxtempyescape.secret");
        preflight.Message.ShouldContain("pgxtempyescape.secret_sequence");
        preflight.Message.ShouldContain("pgxtempyescape.read_secret");
        inspection.IsValid.ShouldBeFalse();
        inspection.Errors.ShouldContain(error => error.Contains("forbidden USAGE on schema 'pgxtempyescape'", StringComparison.Ordinal));
        inspection.Errors.ShouldContain(error => error.Contains("forbidden SELECT access to 'pgxtempyescape.secret'", StringComparison.Ordinal));
        inspection.Errors.ShouldContain(error => error.Contains("sequence 'pgxtempyescape.secret_sequence'", StringComparison.Ordinal));
        inspection.Errors.ShouldContain(error => error.Contains("pgxtempyescape.read_secret", StringComparison.Ordinal));
        inspection.Errors.ShouldContain(error => error.Contains("schema/database CREATE", StringComparison.Ordinal));
    }

    private sealed class InspectionDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlContainer? container;
        private readonly string administratorConnection;
        private readonly string databaseName;
        private readonly string connectionString;
        public string ConnectionString => connectionString;
        public string RuntimeConnection => new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = RuntimeRole,
            Password = runtimePassword
        }.ConnectionString;
        public string RuntimeRole { get; }
        public string OwnerRole { get; }
        private readonly string runtimePassword;

        private InspectionDatabase(PostgreSqlContainer? container, string administratorConnection)
        {
            this.container = container;
            this.administratorConnection = administratorConnection;
            string suffix = Guid.NewGuid().ToString("N");
            databaseName = $"isolation_{suffix}";
            RuntimeRole = $"runtime_{suffix}";
            OwnerRole = $"owner_{suffix}";
            runtimePassword = $"runtime-password-{suffix}";
            connectionString = new NpgsqlConnectionStringBuilder(administratorConnection) { Database = databaseName, Pooling = false }.ConnectionString;
        }

        public static async Task<InspectionDatabase> CreateAsync()
        {
            string? administrator = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
            PostgreSqlContainer? container = null;
            if (string.IsNullOrWhiteSpace(administrator))
            {
                container = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
                await container.StartAsync();
                administrator = container.GetConnectionString();
            }
            InspectionDatabase result = new(container, administrator);
            await result.ExecuteAsync($"CREATE DATABASE {result.databaseName}", administrator);
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(administrator);
            await result.ExecuteAsync($"""
                CREATE ROLE {result.RuntimeRole} LOGIN PASSWORD '{result.runtimePassword}'
                  NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                CREATE ROLE {result.OwnerRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                REVOKE TEMPORARY ON DATABASE {result.databaseName} FROM PUBLIC;
                CREATE SCHEMA app;
                CREATE SCHEMA platform;
                REVOKE ALL ON SCHEMA public FROM PUBLIC;
                REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
                CREATE TABLE app.projects ("Id" uuid PRIMARY KEY, "OrganizationId" uuid NOT NULL);
                CREATE INDEX ON app.projects ("OrganizationId");
                ALTER TABLE app.projects ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.projects FORCE ROW LEVEL SECURITY;
                CREATE POLICY projects_organization_isolation ON app.projects
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE TABLE platform.audit_entries ("OrganizationId" uuid NOT NULL, "ActorId" uuid NOT NULL);
                ALTER TABLE platform.audit_entries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.audit_entries FORCE ROW LEVEL SECURITY;
                CREATE POLICY audit_organization_isolation ON platform.audit_entries
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY audit_projection_worker_read ON platform.audit_entries FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                CREATE TABLE platform.audit_intents (
                  "Id" uuid PRIMARY KEY,
                  "OrganizationId" uuid NOT NULL,
                  "ActorId" uuid NOT NULL);
                ALTER TABLE platform.audit_intents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.audit_intents FORCE ROW LEVEL SECURITY;
                CREATE POLICY audit_intents_append ON platform.audit_intents FOR INSERT TO trykatch_org_runtime
                  WITH CHECK (
                    "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY audit_intents_worker_read ON platform.audit_intents FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                CREATE TABLE platform.outbox_messages ("Id" uuid);
                GRANT USAGE ON SCHEMA app, platform TO {result.RuntimeRole}, {result.OwnerRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON app.projects TO {result.RuntimeRole};
                GRANT SELECT, INSERT ON platform.audit_entries TO {result.RuntimeRole};
                GRANT INSERT ON platform.audit_intents TO {result.RuntimeRole};
                GRANT INSERT ON platform.outbox_messages TO {result.RuntimeRole};
                """);
            return result;
        }

        public Task<PostgresIsolationInspection> InspectAsync() =>
            PostgresIsolationInspector.InspectAsync(connectionString, RuntimeRole, [new ProjectsModule().Descriptor]);

        public async Task ExecuteAsync(string sql, string? connection = null)
        {
            await using NpgsqlConnection database = new(connection ?? connectionString);
            await database.OpenAsync();
            await using NpgsqlCommand command = new(sql, database);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteAsync($"DROP DATABASE {databaseName} WITH (FORCE)", administratorConnection);
                await ExecuteAsync($"DROP ROLE {RuntimeRole}, {OwnerRole}", administratorConnection);
            }
            finally
            {
                if (container is not null) await container.DisposeAsync();
            }
        }
    }
}
