---
title: Production observability
description: Configure and qualify the OpenTelemetry, Collector, Grafana, Loki, Tempo, and Prometheus path.
---

## Deployment authority

Run `dotnet run --project src/API/Horizon.AppHost/Horizon.AppHost.csproj` for local development and tests. AppHost run mode/DCP is not a production orchestrator, and the Aspire Dashboard is local and ephemeral. ServiceDefaults is deployed with the API. AppHost publish mode may become a future CI input, but the template has no production publisher; checked-in Compose remains the deployment authority until another target adapter is compared and qualified in staging.

Before Compose startup, replace `.env.example` placeholders and set `TRYKATCH_RELEASE_VERSION` to an immutable release identifier. Collector, Loki, Tempo, and Prometheus stay on the private Docker network. Grafana binds to loopback and belongs behind authenticated HTTPS. Backend-only deployments must deny public `/health/*` access to the directly published API.

## Signals, identity, and sampling

Serilog is the only `ILogger` provider and OTLP log exporter. The OpenTelemetry SDK exports HTTP, runtime, Npgsql, and outbox metrics/traces. All signals share `service.name`, `service.namespace`, `service.version`, `service.instance.id`, and `deployment.environment.name`. `OTEL_SERVICE_NAME` has highest service-name precedence; approved identity values in `OTEL_RESOURCE_ATTRIBUTES` override application fallbacks. Conflicting signal-specific endpoints, protocols, or headers fail startup without exposing values.

Compose and AppHost explicitly use HTTP/protobuf. HTTPS and gRPC use normal certificate validation. Supply authentication via `OTEL_EXPORTER_OTLP_HEADERS_FILE`; never put production tokens in source or diagnostic output.

Root sampling is parent-based trace-ID ratio: 1.0 for Development and 0.10 for Staging/Production by default. Sampled remote parents can increase intake. Head sampling cannot recover discarded errors or slow traces, so sanitized error logs and metrics remain the reliable unsampled controls. Audit correctness never depends on traces.

Successful `/health/live` and `/health/ready` probes are excluded from request logs, request metrics, and server spans. Readiness depends on PostgreSQL, not telemetry. Collector/backend failure must not block application startup or readiness.

## Privacy and transport

Application logs are projected before console and OTLP export. Approved fields are stable event IDs/templates, route templates, method/status, bounded outcome, exception type, selected database operation metadata, and correlation IDs. Raw paths/queries, headers, bodies, credentials, URLs carrying tokens, connection strings, SQL/parameters, email/names, actor/organization IDs, arbitrary baggage/destructuring, and exception messages are prohibited. New fields require privacy and cardinality review.

The Collector repeats fail-closed allowlisting and sanitization before batching and persistent queues. Plaintext is supported only within the declared private single-host network. For authenticated TLS, provision a server certificate with an `otel-collector` SAN, CA, token, and encoded headers file, then run:

```bash
docker compose -f compose.yml -f compose.observability-tls.yml up -d
```

Any hop leaving the host/trust domain requires authenticated TLS, trusted CA material, and secret-backed credentials. No certificate-validation bypass is supported.

## Retention, recovery, and alerts

Starter retention is Prometheus 15 days/5 GB, Loki 7 days with Compactor deletion enabled, and Tempo 24 hours. Keep at least 20% deployment-disk headroom and add host capacity monitoring. File-backed Collector queues hold 2,000 batches per log/trace exporter with a ten-minute retry horizon and 1 GiB file-store cap. These are qualification starting points, not measured guarantees. Queues can duplicate delivery and do not cover SDK buffers, exhausted retries, overflow/full disk, forced writes, missed Prometheus scrapes, or host/storage loss.

Grafana dashboards and alerts are service/environment scoped. Provisioned warnings cover Collector availability, queue pressure, rejection/export failure, application scrape failure, API error rate/latency, and outbox failures. Thresholds are tunable warnings, not SLOs; configure and prove a staging contact point separately.

## Qualification

Run `scripts/test-observability.sh` for pinned configuration validation. Production still requires recorded staging evidence for three-signal correlation, planted-secret absence with positive controls, TLS/auth failures, unavailable-Collector startup, graceful/forced restart and queue recovery/loss, shortened retention, cardinality across two services/environments, five-minute backend outage capacity at the intended load, and alert delivery/recovery. Record hardware, workload, versions, counts, disk, memory, and latency. Ingress, secret-store operation, backup/PITR, host loss, and capacity beyond the measured workload remain deployment-owned.
