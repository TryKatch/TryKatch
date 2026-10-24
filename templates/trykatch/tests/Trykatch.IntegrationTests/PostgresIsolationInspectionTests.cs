using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Persistence.Migrations.Platform;
using Trykatch.Infrastructure.Persistence.Migrations.Application;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Identity.Persistence.Migrations;

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
    public async Task QuartzTablesAreForcedAndDeniedToOrganizationRuntimeRole()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await using NpgsqlConnection connection = new(database.RuntimeConnection);
        await connection.OpenAsync();

        await using NpgsqlCommand readQuartz = new("SELECT 1 FROM infrastructure.qrtz_job_details LIMIT 1", connection);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => readQuartz.ExecuteScalarAsync());
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection administrator = new(database.ConnectionString);
        await administrator.OpenAsync();
        await using NpgsqlCommand forcedRls = new($"""
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'infrastructure'
              AND c.relname = ANY(ARRAY[{string.Join(",", QuartzSchemaContract.Tables.Select(table => $"'{table}'"))}])
              AND c.relrowsecurity AND c.relforcerowsecurity
            """, administrator);
        Convert.ToInt32(await forcedRls.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(QuartzSchemaContract.Tables.Length);
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
        await database.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE identity.\"UndeclaredQuoted\" (id uuid)");

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
            GRANT USAGE ON SCHEMA identity TO {identity};
            GRANT SELECT, INSERT, UPDATE ON identity.email_deliveries TO {identity};
            GRANT USAGE ON SCHEMA infrastructure TO {outbox};
            GRANT SELECT, INSERT ON infrastructure.module_event_deliveries TO {outbox};
            GRANT USAGE ON SCHEMA platform TO {outbox};
            GRANT SELECT, UPDATE ON platform.outbox_messages TO {outbox};
            GRANT SELECT ON platform.audit_intents TO {outbox};
            GRANT SELECT, INSERT ON platform.audit_entries TO {outbox};
            GRANT SELECT ON platform.outbox_replay_requests TO {outbox};
            GRANT SELECT, INSERT ON platform.outbox_recovery_events TO {outbox};
            GRANT USAGE ON SCHEMA platform TO {platform};
            GRANT SELECT, INSERT ON platform.outbox_replay_requests TO {platform};
            GRANT SELECT ON platform.outbox_recovery_events TO {platform};
            """);
        try
        {
            RuntimeDatabaseRoles roles = new(database.RuntimeRole, platform, identity, outbox);
            (await PostgresIsolationInspector.InspectAsync(
                database.ConnectionString, roles, [new ProjectsModule().Descriptor])).Errors.ShouldBeEmpty();

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
        (await database.InspectAsync()).IsValid.ShouldBeTrue();
        await using PlatformDbContext owner = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.ConnectionString).Options);
        // This behavior test replaces the inspector's minimal stub with the full control-plane model.
        await database.ExecuteAsync("DROP TABLE platform.audit_intents");
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
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => tenant.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO platform.membership_roles (\"MembershipId\", \"RoleId\") VALUES ({membershipA.Id}, {roleB.Id})"));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
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

    [TestMethod]
    public async Task OperationalOwnershipHasNoImplicitGrants()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await using NpgsqlConnection owner = new(database.ConnectionString);
        await owner.OpenAsync();
        await using (NpgsqlCommand exists = new("SELECT to_regclass('infrastructure.module_work_items')::text", owner))
            (await exists.ExecuteScalarAsync()).ShouldBe("infrastructure.module_work_items");
        await using (NpgsqlCommand completionsExist = new("SELECT to_regclass('app.module_work_completions')::text", owner))
            (await completionsExist.ExecuteScalarAsync()).ShouldBe("app.module_work_completions");
        await using (NpgsqlCommand before = new("SELECT has_table_privilege('trykatch_outbox_worker', 'infrastructure.module_work_items', 'SELECT')", owner))
            (await before.ExecuteScalarAsync()).ShouldBe(false);

        await database.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE identity.\"AspNetUsers\" (\"Id\" uuid PRIMARY KEY)");
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString,
            new ProjectsModule().Descriptor.DataResources.Select(resource => new InstalledDataResource("projects", resource)));
        RuntimeDatabaseRoles roles = new("trykatch_org_runtime", "trykatch_platform_runtime", "trykatch_identity_runtime", "trykatch_outbox_worker");
        await RuntimeRoleProvisioner.ProvisionAsync(database.ConnectionString, roles);
        (await PostgresIsolationInspector.InspectAsync(database.ConnectionString, roles,
            [new ProjectsModule().Descriptor])).Errors.ShouldBeEmpty();
        await using NpgsqlCommand privileges = new("""
            SELECT has_table_privilege('trykatch_work_coordinator', 'app.projects', 'SELECT'),
                   has_table_privilege('trykatch_org_runtime', 'infrastructure.module_work_items', 'SELECT'),
                   has_table_privilege('trykatch_org_runtime', 'infrastructure.module_work_items', 'INSERT'),
                   has_table_privilege('trykatch_org_runtime', 'app.module_event_inbox', 'DELETE'),
                   has_table_privilege('trykatch_outbox_worker', 'infrastructure.module_event_deliveries', 'INSERT'),
                   has_table_privilege('trykatch_org_runtime', 'app.module_work_completions', 'SELECT'),
                   has_table_privilege('trykatch_org_runtime', 'app.module_work_completions', 'INSERT'),
                   has_table_privilege('trykatch_org_runtime', 'app.module_work_completions', 'UPDATE'),
                   has_table_privilege('trykatch_org_runtime', 'app.module_work_completions', 'DELETE'),
                   has_table_privilege('trykatch_platform_runtime', 'app.module_work_completions', 'SELECT'),
                   has_table_privilege('trykatch_identity_runtime', 'app.module_work_completions', 'SELECT'),
                   has_table_privilege('trykatch_outbox_worker', 'app.module_work_completions', 'SELECT'),
                   has_table_privilege('trykatch_work_coordinator', 'app.module_work_completions', 'SELECT')
            """, owner);
        await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeFalse();
        reader.GetBoolean(1).ShouldBeFalse();
        reader.GetBoolean(2).ShouldBeTrue();
        reader.GetBoolean(3).ShouldBeFalse();
        reader.GetBoolean(4).ShouldBeTrue();
        reader.GetBoolean(5).ShouldBeTrue();
        reader.GetBoolean(6).ShouldBeTrue();
        reader.GetBoolean(7).ShouldBeFalse();
        reader.GetBoolean(8).ShouldBeFalse();
        reader.GetBoolean(9).ShouldBeFalse();
        reader.GetBoolean(10).ShouldBeFalse();
        reader.GetBoolean(11).ShouldBeFalse();
        reader.GetBoolean(12).ShouldBeFalse();
    }

    [TestMethod]
    public async Task OperationalCompletionMarkerEnforcesOrganizationAndActorScope()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE identity.\"AspNetUsers\" (\"Id\" uuid PRIMARY KEY)");
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString,
            new ProjectsModule().Descriptor.DataResources.Select(resource => new InstalledDataResource("projects", resource)));
        RuntimeDatabaseRoles roles = new("trykatch_org_runtime", "trykatch_platform_runtime", "trykatch_identity_runtime", "trykatch_outbox_worker");
        await RuntimeRoleProvisioner.ProvisionAsync(database.ConnectionString, roles);

        Guid organizationA = Guid.NewGuid();
        Guid organizationB = Guid.NewGuid();
        Guid actorA = Guid.NewGuid();
        Guid workA = Guid.NewGuid();
        await using (NpgsqlConnection owner = new(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using NpgsqlCommand seed = new("""
                INSERT INTO app.module_work_completions
                    ("OrganizationId", "WorkId", "OperationKey", "OperationVersion", "ActorId", "PayloadHash", "CompletedAtUtc")
                VALUES (@organization, @work, 'kernel.probe', 1, @actor, repeat('a', 64), CURRENT_TIMESTAMP),
                       (@otherOrganization, @otherWork, 'kernel.probe', 1, @actor, repeat('b', 64), CURRENT_TIMESTAMP)
                """, owner);
            seed.Parameters.AddWithValue("organization", organizationA);
            seed.Parameters.AddWithValue("work", workA);
            seed.Parameters.AddWithValue("actor", actorA);
            seed.Parameters.AddWithValue("otherOrganization", organizationB);
            seed.Parameters.AddWithValue("otherWork", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }

        string organizationConnection = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(database.ConnectionString)).Organization;
        await using NpgsqlConnection connection = new(organizationConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand noScope = new("SELECT count(*) FROM app.module_work_completions", connection))
            (await noScope.ExecuteScalarAsync()).ShouldBe(0L);

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await SetOrganizationActorAsync(connection, transaction, organizationA, actorA);
            await using NpgsqlCommand visible = new("SELECT count(*) FROM app.module_work_completions", connection, transaction);
            (await visible.ExecuteScalarAsync()).ShouldBe(1L);
            await transaction.RollbackAsync();
        }

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await SetOrganizationActorAsync(connection, transaction, organizationA, actorA);
            await using NpgsqlCommand crossOrganization = CompletionInsert(connection, transaction,
                organizationB, Guid.NewGuid(), actorA);
            (await Should.ThrowAsync<PostgresException>(() => crossOrganization.ExecuteNonQueryAsync()))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync();
        }

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await SetOrganizationActorAsync(connection, transaction, organizationA, actorA);
            await using NpgsqlCommand wrongActor = CompletionInsert(connection, transaction,
                organizationA, Guid.NewGuid(), Guid.NewGuid());
            (await Should.ThrowAsync<PostgresException>(() => wrongActor.ExecuteNonQueryAsync()))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync();
        }
    }

    private static async Task SetOrganizationActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid actorId)
    {
        await using NpgsqlCommand context = new(
            "SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)",
            connection,
            transaction);
        context.Parameters.AddWithValue("organization", organizationId.ToString());
        context.Parameters.AddWithValue("actor", actorId.ToString());
        await context.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand CompletionInsert(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid workId,
        Guid actorId)
    {
        NpgsqlCommand command = new("""
            INSERT INTO app.module_work_completions
                ("OrganizationId", "WorkId", "OperationKey", "OperationVersion", "ActorId", "PayloadHash", "CompletedAtUtc")
            VALUES (@organization, @work, 'kernel.probe', 1, @actor, repeat('c', 64), CURRENT_TIMESTAMP)
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("work", workId);
        command.Parameters.AddWithValue("actor", actorId);
        return command;
    }

    [TestMethod]
    public async Task OperationalInspectorRejectsMissingPolicyAndOverbroadGrant()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE identity.\"AspNetUsers\" (\"Id\" uuid PRIMARY KEY)");
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString,
            new ProjectsModule().Descriptor.DataResources.Select(resource => new InstalledDataResource("projects", resource)));
        RuntimeDatabaseRoles roles = new("trykatch_org_runtime", "trykatch_platform_runtime", "trykatch_identity_runtime", "trykatch_outbox_worker");
        await RuntimeRoleProvisioner.ProvisionAsync(database.ConnectionString, roles);
        (await PostgresIsolationInspector.InspectAsync(database.ConnectionString, roles,
            [new ProjectsModule().Descriptor])).IsValid.ShouldBeTrue();
        await database.ExecuteAsync("GRANT DELETE ON app.module_event_inbox TO trykatch_org_runtime");
        PostgresIsolationInspection broad = await PostgresIsolationInspector.InspectAsync(database.ConnectionString, roles,
            [new ProjectsModule().Descriptor]);
        broad.Errors.ShouldContain(error => error.Contains("forbidden DELETE", StringComparison.Ordinal));
        await database.ExecuteAsync("REVOKE DELETE ON app.module_event_inbox FROM trykatch_org_runtime; DROP POLICY module_event_inbox_insert ON app.module_event_inbox");
        PostgresIsolationInspection missing = await PostgresIsolationInspector.InspectAsync(database.ConnectionString, roles,
            [new ProjectsModule().Descriptor]);
        missing.Errors.ShouldContain(error => error.Contains("module_event_inbox_insert", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OperationalInboxEnforcesCurrentScopeActorAndConsumerDeduplication()
    {
        await using InspectionDatabase database = await InspectionDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE identity.\"AspNetUsers\" (\"Id\" uuid PRIMARY KEY)");
        await InstalledSchemaCatalog.SynchronizeAsync(database.ConnectionString,
            new ProjectsModule().Descriptor.DataResources.Select(resource => new InstalledDataResource("projects", resource)));
        RuntimeDatabaseRoles roles = new("trykatch_org_runtime", "trykatch_platform_runtime", "trykatch_identity_runtime", "trykatch_outbox_worker");
        await RuntimeRoleProvisioner.ProvisionAsync(database.ConnectionString, roles);
        Guid organizationA = Guid.NewGuid();
        Guid actorA = Guid.NewGuid();
        Guid message = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO app.module_event_inbox ("OrganizationId", "MessageId", "ConsumerKey", "SchemaVersion", "ActorId", "ProcessedAtUtc")
            VALUES ('{organizationA}', '{message}', 'kernel.probe', 1, '{actorA}', CURRENT_TIMESTAMP)
            """);
        string organizationConnection = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(database.ConnectionString)).Organization;
        await using NpgsqlConnection connection = new(organizationConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand missing = new("SELECT count(*) FROM app.module_event_inbox", connection))
            (await missing.ExecuteScalarAsync()).ShouldBe(0L);
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using NpgsqlCommand context = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
            context.Parameters.AddWithValue("organization", Guid.NewGuid().ToString());
            context.Parameters.AddWithValue("actor", actorA.ToString());
            await context.ExecuteNonQueryAsync();
            await using NpgsqlCommand hidden = new("SELECT count(*) FROM app.module_event_inbox", connection, transaction);
            (await hidden.ExecuteScalarAsync()).ShouldBe(0L);
            await using NpgsqlCommand wrongScope = new($"""
                INSERT INTO app.module_event_inbox ("OrganizationId", "MessageId", "ConsumerKey", "SchemaVersion", "ActorId", "ProcessedAtUtc")
                VALUES ('{organizationA}', '{Guid.NewGuid()}', 'kernel.probe', 1, '{actorA}', CURRENT_TIMESTAMP)
                """, connection, transaction);
            (await Should.ThrowAsync<PostgresException>(() => wrongScope.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");
            await transaction.RollbackAsync();
        }
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using NpgsqlCommand context = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
            context.Parameters.AddWithValue("organization", organizationA.ToString());
            context.Parameters.AddWithValue("actor", actorA.ToString());
            await context.ExecuteNonQueryAsync();
            await using NpgsqlCommand duplicate = new($"""
                INSERT INTO app.module_event_inbox ("OrganizationId", "MessageId", "ConsumerKey", "SchemaVersion", "ActorId", "ProcessedAtUtc")
                VALUES ('{organizationA}', '{message}', 'kernel.probe', 2, '{actorA}', CURRENT_TIMESTAMP)
                """, connection, transaction);
            (await Should.ThrowAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync())).SqlState.ShouldBe("23505");
            await transaction.RollbackAsync();
        }
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using NpgsqlCommand context = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
            context.Parameters.AddWithValue("organization", organizationA.ToString());
            context.Parameters.AddWithValue("actor", actorA.ToString());
            await context.ExecuteNonQueryAsync();
            await using NpgsqlCommand wrongActor = new($"""
                INSERT INTO app.module_event_inbox ("OrganizationId", "MessageId", "ConsumerKey", "SchemaVersion", "ActorId", "ProcessedAtUtc")
                VALUES ('{organizationA}', '{Guid.NewGuid()}', 'kernel.probe', 1, '{Guid.NewGuid()}', CURRENT_TIMESTAMP)
                """, connection, transaction);
            (await Should.ThrowAsync<PostgresException>(() => wrongActor.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");
            await transaction.RollbackAsync();
        }
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
                CREATE SCHEMA identity;
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
                CREATE TABLE platform.outbox_replay_requests (
                  "RequestId" uuid PRIMARY KEY, "MessageId" uuid NOT NULL,
                  "ExpectedFailedGeneration" integer NOT NULL, "ActorId" uuid NOT NULL, "RequestedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP);
                ALTER TABLE platform.outbox_replay_requests ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.outbox_replay_requests FORCE ROW LEVEL SECURITY;
                CREATE POLICY outbox_replay_requests_platform_read ON platform.outbox_replay_requests FOR SELECT TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime');
                CREATE POLICY outbox_replay_requests_platform_insert ON platform.outbox_replay_requests FOR INSERT TO trykatch_platform_runtime
                  WITH CHECK (current_user = 'trykatch_platform_runtime' AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid AND "RequestedAt" = CURRENT_TIMESTAMP);
                CREATE POLICY outbox_replay_requests_worker_read ON platform.outbox_replay_requests FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                CREATE TABLE platform.outbox_recovery_events (
                  "Id" uuid PRIMARY KEY, "MessageId" uuid NOT NULL, "ReplayGeneration" integer NOT NULL, "Outcome" text NOT NULL,
                  "FailureCode" text NULL, "FailureType" text NULL, "OccurredAt" timestamptz NOT NULL,
                  "RequestId" uuid NULL, "ActorId" uuid NULL);
                ALTER TABLE platform.outbox_recovery_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.outbox_recovery_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY outbox_recovery_events_platform_read ON platform.outbox_recovery_events FOR SELECT TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime');
                CREATE POLICY outbox_recovery_events_worker_read ON platform.outbox_recovery_events FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                CREATE POLICY outbox_recovery_events_worker_insert ON platform.outbox_recovery_events FOR INSERT TO trykatch_outbox_worker
                  WITH CHECK (current_user = 'trykatch_outbox_worker');
                GRANT USAGE ON SCHEMA app, platform TO {result.RuntimeRole}, {result.OwnerRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON app.projects TO {result.RuntimeRole};
                GRANT SELECT, INSERT ON platform.audit_entries TO {result.RuntimeRole};
                GRANT INSERT ON platform.audit_intents TO {result.RuntimeRole};
                GRANT INSERT ON platform.outbox_messages TO {result.RuntimeRole};
                GRANT SELECT, INSERT ON platform.outbox_replay_requests TO trykatch_platform_runtime;
                GRANT SELECT ON platform.outbox_recovery_events TO trykatch_platform_runtime;
                GRANT SELECT ON platform.outbox_replay_requests TO trykatch_outbox_worker;
                GRANT SELECT, INSERT ON platform.outbox_recovery_events TO trykatch_outbox_worker;
                """);
            await using ApplicationDbContext operational = new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(result.connectionString).Options);
            MigrationOperation[] operations = new AddModuleExecutionContracts().UpOperations
                .Where(operation => operation is not AddColumnOperation).ToArray();
            foreach (var command in operational.GetService<IMigrationsSqlGenerator>().Generate(operations, operational.Model))
                await result.ExecuteAsync(command.CommandText);
            MigrationOperation[] workExecutionOperations = new AddOrganizationWorkExecution().UpOperations
                .Where(operation => operation is CreateTableOperation or CreateIndexOperation or SqlOperation
                    || operation is AddColumnOperation { Table: "module_work_items" })
                .ToArray();
            foreach (var command in operational.GetService<IMigrationsSqlGenerator>().Generate(workExecutionOperations, operational.Model))
                await result.ExecuteAsync(command.CommandText);
            MigrationOperation[] durableJobOperations = new AddDurableModuleJobs().UpOperations.ToArray();
            foreach (var command in operational.GetService<IMigrationsSqlGenerator>().Generate(durableJobOperations, operational.Model))
                await result.ExecuteAsync(command.CommandText);
            foreach (var command in operational.GetService<IMigrationsSqlGenerator>().Generate(
                         new AddOrganizationEmailDelivery().UpOperations, operational.Model))
                await result.ExecuteAsync(command.CommandText);
            foreach (var command in operational.GetService<IMigrationsSqlGenerator>().Generate(
                         new AddIdentityEmailDelivery().UpOperations, operational.Model))
                await result.ExecuteAsync(command.CommandText);
            await result.ExecuteAsync($"""
                GRANT USAGE ON SCHEMA infrastructure TO {result.RuntimeRole}, trykatch_work_coordinator, trykatch_outbox_worker;
                GRANT INSERT ON infrastructure.module_work_items TO {result.RuntimeRole};
                GRANT SELECT, INSERT ON app.module_event_inbox TO {result.RuntimeRole};
                GRANT SELECT, INSERT ON app.module_work_completions TO {result.RuntimeRole};
                GRANT SELECT, INSERT, UPDATE ON infrastructure.module_work_items TO trykatch_work_coordinator;
                GRANT SELECT, UPDATE ON infrastructure.module_event_deliveries TO trykatch_work_coordinator;
                GRANT SELECT, INSERT ON infrastructure.module_event_deliveries TO trykatch_outbox_worker;
                GRANT INSERT ON infrastructure.email_deliveries TO {result.RuntimeRole};
                GRANT USAGE ON SCHEMA identity TO trykatch_identity_runtime;
                """);
            await GrantDurableJobAccessAsync(result);
            return result;
        }

        private static async Task GrantDurableJobAccessAsync(InspectionDatabase database)
        {
            RuntimeDatabaseRoles roles = new(
                "trykatch_org_runtime",
                "trykatch_platform_runtime",
                "trykatch_identity_runtime",
                "trykatch_outbox_worker");
            Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
                .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
            string[] relations = [
                "infrastructure.module_schedule_cursors",
                "infrastructure.module_work_controls",
                "infrastructure.email_deliveries",
                "identity.email_deliveries",
                .. QuartzSchemaContract.Tables.Select(table => "infrastructure." + table)
            ];

            foreach ((RuntimeDatabaseRoleKind kind, string role) in roles.All)
            foreach (string relation in relations)
            {
                IReadOnlySet<string> permissions = RuntimeDatabaseAccessProfiles.PermissionsFor(kind, relation, resources);
                if (permissions.Count == 0) continue;
                await database.ExecuteAsync($"GRANT {string.Join(", ", permissions)} ON {relation} TO {role}");
            }
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
