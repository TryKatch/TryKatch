IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume("flatpack-postgres-data");
IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("flatpackdb", "flatpack");

IResourceBuilder<ContainerResource> collector = builder
    .AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.160.0")
    .WithBindMount("../../deploy/observability/otel-collector.yml", "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(targetPort: 8889, name: "prometheus");

IResourceBuilder<ProjectResource> migrator = builder
    .AddProject<Projects.TemplateProjectIdentifier_Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

IResourceBuilder<ProjectResource> api = builder
    .AddProject<Projects.TemplateProjectIdentifier_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrator)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", collector.GetEndpoint("otlp-http"));

#if FLATPACK_EMAIL
IResourceBuilder<ContainerResource> mailpit = builder
    .AddContainer("mailpit", "axllent/mailpit", "v1.30.4")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpEndpoint(targetPort: 8025, name: "inbox")
    .WithExternalHttpEndpoints();
api.WithEnvironment("Email__Host", "mailpit")
    .WithEnvironment("Email__Port", "1025")
    .WaitFor(mailpit);
#endif

builder.AddContainer("loki", "grafana/loki", "3.7.2")
    .WithBindMount("../../deploy/observability/loki.yml", "/etc/loki/local-config.yaml", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 3100, name: "http");
builder.AddContainer("tempo", "grafana/tempo", "3.0.2")
    .WithBindMount("../../deploy/observability/tempo.yml", "/etc/tempo.yml", isReadOnly: true)
    .WithArgs("-config.file=/etc/tempo.yml")
    .WithHttpEndpoint(targetPort: 3200, name: "http");
builder.AddContainer("prometheus", "prom/prometheus", "v3.14.0")
    .WithBindMount("../../deploy/observability/prometheus.yml", "/etc/prometheus/prometheus.yml", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 9090, name: "http");
builder.AddContainer("grafana", "grafana/grafana", "13.2.1")
    .WithBindMount("../../deploy/observability/grafana", "/etc/grafana/provisioning", isReadOnly: true)
    .WithHttpEndpoint(targetPort: 3000, name: "http");

#if FLATPACK_REACT
builder.AddViteApp("web", "../../web/apps/web")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();
#endif

builder.Build().Run();
