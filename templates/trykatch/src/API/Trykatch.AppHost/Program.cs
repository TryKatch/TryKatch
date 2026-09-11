using Aspire.Hosting.ApplicationModel;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ParameterResource> migratorPassword = builder.AddParameter("migrator-password", "local-migrator-only", secret: true);
IResourceBuilder<ParameterResource> organizationPassword = builder.AddParameter("organization-runtime-password", "local-organization-only", secret: true);
IResourceBuilder<ParameterResource> platformPassword = builder.AddParameter("platform-runtime-password", "local-platform-only", secret: true);
IResourceBuilder<ParameterResource> identityPassword = builder.AddParameter("identity-runtime-password", "local-identity-only", secret: true);
IResourceBuilder<ParameterResource> outboxPassword = builder.AddParameter("outbox-runtime-password", "local-outbox-only", secret: true);

IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithImageTag("18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f")
    .WithEnvironment("TRYKATCH_MIGRATOR_PASSWORD", migratorPassword)
    .WithEnvironment("TRYKATCH_ORG_RUNTIME_PASSWORD", organizationPassword)
    .WithEnvironment("TRYKATCH_PLATFORM_RUNTIME_PASSWORD", platformPassword)
    .WithEnvironment("TRYKATCH_IDENTITY_RUNTIME_PASSWORD", identityPassword)
    .WithEnvironment("TRYKATCH_OUTBOX_WORKER_PASSWORD", outboxPassword)
    .WithBindMount("../../../deploy/postgres/init", "/docker-entrypoint-initdb.d", isReadOnly: true)
    .WithDataVolume("trykatch-app-slug-postgres-data");
IResourceBuilder<PostgresDatabaseResource> database = postgres
    .AddDatabase("database", "trykatch")
    // Aspire creates named databases after the container init scripts finish.
    // The creation script therefore has to assign the migrator as owner itself.
    .WithCreationScript("CREATE DATABASE \"trykatch\" OWNER \"trykatch_migrator\"");
ReferenceExpression migratorConnection = ReferenceExpression.Create(
    $"{database.Resource.ConnectionStringExpression};Username=trykatch_migrator;Password={migratorPassword}");
ReferenceExpression organizationConnection = ReferenceExpression.Create(
    $"{database.Resource.ConnectionStringExpression};Username=trykatch_org_runtime;Password={organizationPassword}");
ReferenceExpression platformConnection = ReferenceExpression.Create(
    $"{database.Resource.ConnectionStringExpression};Username=trykatch_platform_runtime;Password={platformPassword}");
ReferenceExpression identityConnection = ReferenceExpression.Create(
    $"{database.Resource.ConnectionStringExpression};Username=trykatch_identity_runtime;Password={identityPassword}");
ReferenceExpression outboxConnection = ReferenceExpression.Create(
    $"{database.Resource.ConnectionStringExpression};Username=trykatch_outbox_worker;Password={outboxPassword}");

IResourceBuilder<ContainerResource> collector = builder
    .AddDockerfile("otel-collector", "../../../deploy/observability", "otel-collector.Dockerfile")
    .WithBindMount("../../../deploy/observability/otel-collector.yml", "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
    .WithVolume("trykatch-app-slug-otel-queue", "/var/lib/otelcol")
    .WithHttpEndpoint(targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(targetPort: 8888, name: "telemetry")
    .WithHttpEndpoint(targetPort: 8889, name: "prometheus");

IResourceBuilder<ProjectResource> migrator = builder
    .AddProject<Projects.TemplateProjectIdentifier_Migrator>("migrator")
    .WithEnvironment("ConnectionStrings__trykatchdb", migratorConnection)
    .WithEnvironment("Database__OrganizationRuntimeRole", "trykatch_org_runtime")
    .WithEnvironment("Database__PlatformRuntimeRole", "trykatch_platform_runtime")
    .WithEnvironment("Database__IdentityRuntimeRole", "trykatch_identity_runtime")
    .WithEnvironment("Database__OutboxWorkerRole", "trykatch_outbox_worker")
    .WaitFor(database);

IResourceBuilder<ProjectResource> api = builder
    .AddProject<Projects.TemplateProjectIdentifier_Api>("api")
    .WithEnvironment("ConnectionStrings__trykatch-organization", organizationConnection)
    .WithEnvironment("ConnectionStrings__trykatch-platform", platformConnection)
    .WithEnvironment("ConnectionStrings__trykatch-identity", identityConnection)
    .WithEnvironment("ConnectionStrings__trykatch-outbox", outboxConnection)
    .WaitFor(database)
    .WaitForCompletion(migrator)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", collector.GetEndpoint("otlp-http"))
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WithEnvironment("OTEL_TRACES_SAMPLER", "parentbased_traceidratio")
    .WithEnvironment("OTEL_TRACES_SAMPLER_ARG", "1");

if (builder.ExecutionContext.IsRunMode)
{
    api.WithEnvironment("DevelopmentDemo__Enabled", "true")
        .WithEnvironment("DevelopmentDemo__PlatformAdminEmail", "admin@trykatch.net")
        .WithEnvironment("DevelopmentDemo__TenantAdminEmail", "tenant@trykatch.net")
        .WithEnvironment("DevelopmentDemo__Password", "Admin@123")
        .WithEnvironment("DevelopmentDemo__OrganizationName", "Demo Workspace")
        .WithEnvironment("DevelopmentDemo__OrganizationSlug", "demo-workspace");
}

#if TRYKATCH_EMAIL
IResourceBuilder<ContainerResource> mailpit = builder
    .AddContainer("mailpit", "axllent/mailpit", "v1.30.4@sha256:5a49a77c5bdbe7c5474450b4f46348d09949df3695257729c93a30369382d4f6")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpEndpoint(targetPort: 8025, name: "inbox")
    .WithExternalHttpEndpoints();
api.WithEnvironment("Email__Host", "mailpit")
    .WithEnvironment("Email__Port", "1025")
    .WaitFor(mailpit);
#endif

builder.AddContainer("loki", "grafana/loki", "3.7.2@sha256:191d4fdfb7264f16989f0a57f320872620a5a7c2ceeec6229212c4190ec49b86")
    .WithBindMount("../../../deploy/observability/loki.yml", "/etc/loki/local-config.yaml", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 3100, name: "http");
builder.AddContainer("tempo", "grafana/tempo", "3.0.2@sha256:cda87c212d8c584dc0b89e337e7ed648a5100feb657e5d528480ee4fa03dbbe3")
    .WithBindMount("../../../deploy/observability/tempo.yml", "/etc/tempo.yml", isReadOnly: true)
    .WithArgs("-config.file=/etc/tempo.yml")
    .WithHttpEndpoint(targetPort: 3200, name: "http");
builder.AddContainer("prometheus", "prom/prometheus", "v3.14.0@sha256:5ce7540c3c00ef4ab0c9d2c995c6a5b9c421f44b4a115d97a2c7af3b1c21cbb0")
    .WithBindMount("../../../deploy/observability/prometheus.yml", "/etc/prometheus/prometheus.yml", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 9090, name: "http");
builder.AddContainer("grafana", "grafana/grafana", "13.2.1@sha256:f772d434e8fab0049deb2b1b30abd43342bcfca1537614aa8d36080232cf4283")
    .WithBindMount("../../../deploy/observability/grafana", "/etc/grafana/provisioning", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 3000, name: "http");

#if HAS_REACT_UI
builder.AddViteApp("web", "../../../web/apps/web")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();
#endif

builder.Build().Run();
