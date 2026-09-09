# Aspire and OpenTelemetry production guidance

Verified against official Aspire, Microsoft .NET, and OpenTelemetry sources on 2026-09-09. This is guidance, not a Microsoft certification of the repository.

## Short answer

Aspire is suitable for production as an application model, Service Defaults library, and publish/deployment input. `aspire run`, AppHost run mode, and its Developer Control Plane (DCP) are not production orchestrators: Microsoft describes them as development-time orchestration for local development and testing. In publish mode, AppHost emits target-specific artifacts for a production platform to run. See [Aspire architecture](https://aspire.dev/architecture/overview/) and the [deployment model](https://aspire.dev/deployment/deploy-with-aspire/).

Use the stable Aspire release channel for production work. `aspire publish` provides a reviewable, one-way handoff into CI/CD. Aspire models Development, Staging, and Production environments and supports targets including Docker Compose, Kubernetes, Azure Container Apps, and App Service. Qualify `aspire deploy` and the chosen target integration in staging before making it the production deployment authority. See the [Aspire deployment overview](https://aspire.dev/deployment/) and [`aspire publish`](https://learn.microsoft.com/en-us/dotnet/aspire/cli-reference/aspire-publish).

The Aspire Dashboard can run outside local development, but it is designed for development and short-term diagnostics. It stores telemetry in memory and should not replace a durable production observability backend. A nonlocal dashboard requires HTTPS, authenticated frontend access, authenticated OTLP ingestion, private network access, telemetry limits, and upstream rate/concurrency limits. See the [standalone dashboard](https://aspire.dev/dashboard/standalone/) and [security considerations](https://aspire.dev/dashboard/security-considerations/).

TryKatch's runtime split is appropriate: AppHost/DCP for local development and tests; separately deployed API, web, Collector, and telemetry backends in production. AppHost may still generate deployment artifacts or participate in a deployment pipeline.

## Production OpenTelemetry baseline

| Area | Official expectation | TryKatch assessment |
| --- | --- | --- |
| Signals and export | Logs, metrics, and traces are complementary. Service Defaults configures OpenTelemetry and OTLP provides a vendor-neutral export path. [Aspire telemetry](https://aspire.dev/fundamentals/telemetry/), [.NET observability](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) | ServiceDefaults exports traces and metrics, while Serilog exports logs. ASP.NET Core, HttpClient, runtime, Npgsql, and outbox instrumentation provide a good three-signal baseline. Verify correlation and shutdown behavior under Collector failure because logs use a separate sink pipeline. |
| Collector | Production deployments generally place a Collector beside services to offload batching, retry, encryption, and filtering. [Collector guidance](https://opentelemetry.io/docs/collector/) | The API sends OTLP to a Collector, which is the right topology. Application readiness correctly remains independent of observability backends. |
| Resource identity | Set stable `service.name`, `service.namespace`, `service.version`, unique `service.instance.id`, and `deployment.environment.name`. [Service conventions](https://opentelemetry.io/docs/specs/semconv/resource/service/) | Compose sets `OTEL_SERVICE_NAME`; production lacks namespace, build version, and environment attributes. Ensure the .NET and Serilog exporters emit identical resource identity. |
| Sampling | The .NET default is parent-based with always-on roots. Production should use an evidence-based parent-based ratio sampler or Collector tail sampling. [Sampling](https://opentelemetry.io/docs/languages/dotnet/sampling/) | No production sampling policy is configured, so traces are effectively always-on. Audit evidence must remain durable and separate from sampled traces. |
| Sensitive data | The implementer owns minimization and compliance. Collector processors can filter, transform, or allowlist telemetry. [Sensitive data](https://opentelemetry.io/docs/security/handling-sensitive-data/) | There is no Collector sanitization processor. Define prohibited fields and add planted-secret tests across stdout, Loki, Tempo, and Grafana. |
| Transport security | Use secure channels, authentication, secret storage, least privilege, and narrow receiver exposure. [Collector security](https://opentelemetry.io/docs/security/config-best-practices/) | OTLP is private to the Compose network, which limits exposure, but receiver and backend links use plaintext without authentication. Add TLS and workload/API-key authentication whenever telemetry crosses a host or trust boundary. |
| Health noise | Health probes should be excluded from traces and high-frequency metrics where appropriate, and production probe routes should be deliberately exposed. [Health checks](https://aspire.dev/fundamentals/health-checks/) | `/health/*` is excluded from traces, but health HTTP metrics and request logs are not suppressed and probe routes have no explicit isolation policy. |
| Resilience | Exporters should use measured queues/retries; persistent WAL storage is appropriate when loss across Collector restarts is unacceptable. [Collector resilience](https://opentelemetry.io/docs/collector/resiliency/) | Memory limiting, batching, and retries are present. Queue sizing, durability, Collector self-monitoring, and outage/restart tests are not yet defined. |
| Cardinality | Metric attributes must stay bounded; avoid raw URLs, request IDs, user IDs, organization IDs, and unbounded messages. [.NET metrics guidance](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation) | Review all current and future tenant-aware dimensions, and monitor backend series growth before using the dashboards for SLOs. |

## Repository verdict

The design is aligned with Microsoft and OpenTelemetry guidance and is stronger than a development-only demo: it uses all three signals, OTLP, a Collector, batch and memory processors, retries, persistent telemetry backends, custom trace sources, explicit production `service.name`, and a production runtime separated from AppHost/DCP.

It is not yet production-qualified until these items are specified and tested:

1. Complete, consistent resource identity across .NET and Serilog exporters.
2. A parent-based ratio sampling policy and explicit handling of slow/error traces.
3. Source minimization, Collector redaction/allowlisting, and planted-secret tests.
4. A documented trust boundary with TLS/authentication for nonlocal telemetry hops.
5. Health metric/log suppression and protected probe exposure.
6. Queue, retry, shutdown, restart, and backend-outage tests with known loss budgets.
7. Cardinality, retention, alert, capacity, and cost limits under representative load.

