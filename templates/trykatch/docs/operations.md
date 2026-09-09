# Operations

## Supported deployment roles

Use `dotnet run --project src/TrykatchApp.AppHost` for local development and bounded integration tests. AppHost run mode and DCP are development-time orchestration; the Aspire Dashboard is local, ephemeral, and not an operational record. ServiceDefaults ships with the API. Although AppHost publish mode can be an input to a future target-specific deployment pipeline, this template has no configured production publisher and does not claim that `aspire publish` is equivalent to the maintained Compose deployment.

The checked-in `compose.yml` (React) or generated `compose.yml` (backend-only) is the production authority for this release. A target platform runs those resources. Before adopting an Aspire publisher, compare its networks, secret references, volumes, identity, migrator ordering, TLS, retention, and ingress against Compose and qualify the generated artifact in staging. Demo users and credentials are attached only in AppHost run mode.

## Production startup and trust boundary

Copy `.env.example` to `.env`, replace every placeholder, and set `TRYKATCH_RELEASE_VERSION` to an immutable release tag or commit identifier. Place separate PKCS#12 signing and encryption certificates under `${TRYKATCH_SECRETS_PATH}`. Keep `.env`, certificates, database credentials, SMTP credentials, object-storage keys, and telemetry tokens out of Git and container images.

The base telemetry transport is plaintext only inside one controlled Docker host and its private Compose network. Collector, Loki, Tempo, and Prometheus publish no host ports. Grafana binds to `127.0.0.1` by default, requires a non-default password, and should be exposed only through an authenticated HTTPS operational ingress. The React proxy must not expose `/health`; backend-only deployments publish API port 8080 directly and must deny `/health/*` at their ingress while retaining container health checks. Any telemetry hop leaving the host or trust domain requires authenticated TLS.

The API depends on PostgreSQL migration completion, not on the Collector or a telemetry backend. An unavailable Collector must not prevent business startup or change `/health/ready`, which depends only on PostgreSQL. `/health/live` is process-only. Successful operational probes are excluded from request logs, ASP.NET request metrics, and server spans; failed probes remain visible. Aggregate runtime, process, and pool metrics are not attributable to individual probes.

## Identity, export, and sampling

ServiceDefaults resolves one resource for traces, metrics, and Serilog OTLP logs:

1. `OTEL_SERVICE_NAME` overrides `service.name` in `OTEL_RESOURCE_ATTRIBUTES`.
2. The other approved standard attributes in `OTEL_RESOURCE_ATTRIBUTES` override application defaults: `service.namespace`, `service.version`, `service.instance.id`, and `deployment.environment.name`.
3. `TRYKATCH_RELEASE_VERSION` supplies the version fallback; the assembly informational version is a development fallback.
4. The .NET environment maps to `development`, `staging`, `production`, or `other`; each host gets a unique generated instance ID when Aspire or deployment configuration does not provide one.

Additional resource attributes are rejected until reviewed. The supported common transport variables are `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, and either inline `OTEL_EXPORTER_OTLP_HEADERS` or the safer `OTEL_EXPORTER_OTLP_HEADERS_FILE`. Signal-specific settings must equal the common values; conflicting destinations fail startup without printing values or headers. HTTP/protobuf is explicit in Compose and AppHost; gRPC is also supported and never inferred from a port. HTTPS uses normal platform certificate validation; there is no certificate-validation bypass.

Root traces use parent-based trace-ID-ratio sampling: 1.0 in Development and 0.10 in Staging/Production unless `OTEL_TRACES_SAMPLER_ARG` changes it. A sampled remote parent can increase intake above the root ratio. Head sampling cannot recover a discarded error or slow trace, so some error logs have no stored trace. Sanitized error logs and metrics remain unsampled. Audit records are persisted separately and never depend on traces. Tail sampling is intentionally deferred.

## Telemetry privacy contract

Serilog remains the sole `ILogger` provider and only OTLP log exporter. Every console and OTLP log is projected before either sink. The approved export schema contains stable event IDs/templates, route templates, HTTP method/status, bounded outcomes, exception type, selected database-operation metadata, and trace/span correlation. Strings and metric cardinality are bounded. Request raw paths and query strings are not logged; unmatched requests use `unmatched`. Outbox failure telemetry retains an exception type and bounded outcome, never exception text. Opaque outbox message IDs may be trace/log metadata but are never metric or Loki index labels.

Do not add authorization/cookie headers, bodies, credentials, connection strings, raw SQL or parameters, reset/invitation URLs, email, personal names, arbitrary baggage, actor/organization IDs, arbitrary destructuring, or free-form exception messages. A new log template or telemetry field needs a privacy and cardinality review. Database business/audit/outbox retention is unchanged by this policy.

The Collector independently applies a fail-closed resource and attribute allowlist, clears log bodies and span status descriptions, sanitizes names, and masks planted credential/PII patterns before batching or persistent queues. Processor order is `memory_limiter → transform/privacy → redaction/privacy → batch → exporter`; transformation errors drop the payload. Collector payload-debug logging is disabled.

## TLS and authentication extension

Base Compose is immediately usable on the private host network. For authenticated TLS ingestion, provision a certificate whose SAN includes `otel-collector`, its key, the issuing CA, a random bearer token, and an application headers file under `${TRYKATCH_TELEMETRY_SECRETS_PATH}`. The token file contains the token only; the headers file contains `Authorization=Bearer%20<token>`. Then run:

```bash
docker compose -f compose.yml -f compose.observability-tls.yml up -d
```

The overlay mounts secrets read-only, trusts only the supplied CA in the API, and uses `otel-collector.tls.yml`. Wrong/absent tokens and untrusted certificates fail export without failing API readiness. Rotate tokens with an overlap or coordinated restart and remove old material afterward.

For a Collector exporter that leaves the trust domain, use an `https://` endpoint, a mounted CA file, and a client authentication extension backed by a secret file. Do not put tokens in YAML. HTTPS without backend authentication is insufficient; workload identity or mTLS can replace bearer auth without changing application callers.

## Retention, queues, and loss windows

The starter policy is Prometheus 15 days with a 5 GB TSDB size ceiling, Loki 7 days with Compactor retention/deletion enabled, and Tempo 24 hours. Container stdout rotates at 10 MB × 3 files. Time retention is not a hard filesystem quota. Provision storage so the complete deployment remains at least 20% free and add host/volume-capacity monitoring; this stack does not invent disk alerts without host metrics.

Logs and traces use file-backed Collector queues on `collector-storage`: 2,000 request batches per exporter, four consumers, five-second exporter timeout, and a ten-minute retry horizon. The file store is capped at 1 GiB, fsyncs writes, and compacts after rebound. These are unqualified starting values, not measured capacity claims. Persistent queues protect only accepted/enqueued records across Collector restarts and can duplicate delivery. SDK memory queues, unflushed batches, exhausted retries, overflow/full disk, forced termination during writes, and host/storage loss remain loss windows. Prometheus pull has no durable outbound queue; missed scrapes are missing.

At staging, measure log volume and sampled-parent traffic at 20 requests/second with a five-minute backend outage, then derive queue capacity from measured records/bytes per second × 300 seconds × 2. Record memory/disk use against the 512 MiB Collector limit. Verify graceful API and Collector shutdown separately from forced restarts.

## Dashboards and alerts

Grafana provisions service/environment-scoped HTTP, database, outbox, and Collector panels. Only `service.name`, `service.namespace`, and `deployment.environment.name` become bounded application metric labels; per-process identity remains available through exporter target metadata/job-instance mapping. Loki indexes only service, namespace, and environment. Version, instance, message, and trace IDs remain structured metadata.

Provisioned warnings cover Collector availability, queue pressure above 80%, rejected/failed telemetry, application-metric scrape failure, API 5xx rate above 5% with at least 0.2 requests/second, p95 latency above one second, and outbox failures. Thresholds are tunable warnings, not SLOs. No notification destination is embedded. A staging operator must configure a contact point and prove firing, delivery, no-data/error behavior, and recovery before production.

### Collector unavailable

Verify the API and PostgreSQL first, then restore the Collector. Inspect `up{job="otel-collector"}`, container logs, volume permissions, and port 8888 on the private network.

### Queue pressure

Inspect `otelcol_exporter_queue_size`, capacity, send-failed metrics, backend health, retry age, and free disk. Restore the backend before the retry horizon; do not delete the Collector volume during recovery.

### Telemetry loss

Treat receiver refusal, exporter failure, overflow, or storage-full metrics as measured loss risk. Preserve sanitized diagnostics and compare backend counts with workload controls.

### Metrics scrape

Port 8889 is application metrics and 8888 is Collector self-telemetry. A healthy API can coexist with either scrape being unavailable.

### API errors and latency

Scope by service/environment and require the minimum traffic guard. Use sanitized logs and sampled traces for diagnosis; absence of a trace is expected under head sampling.

### Outbox

Check the bounded `outcome` metric and database outbox state. Do not log payloads or exception messages. Downstream adapters must preserve and deduplicate the message ID.

## Verification, rollback, and deployment-owned qualification

Run `scripts/test-observability.sh` to render both Compose variants and the TLS overlay, validate the pinned Collector/Prometheus/Loki/Tempo configurations, and check provisioning files. Pull requests run fast contract tests for endpoint-free startup, invalid/conflicting settings, sampling parent behavior, allowlisting, and safe outbox errors. A successful local/configuration test is not staging qualification.

Before production, a staging deployment must prove all three signals and correlation; planted-secret absence with positive controls across stdout, Collector diagnostics, Loki, Tempo, and Grafana; TLS/auth failure isolation; unavailable-Collector startup; persistent-queue restart/recovery and measured forced-restart/overflow/full-disk loss; shortened-retention deletion; two-service/environment isolation and cardinality; five-minute outage capacity; AppHost local signal delivery; and alert contact-point delivery/recovery. Record hardware, workload, image versions, counts, latency, disk, and memory. Backup/PITR, host loss, ingress, secret-store operations, and capacity beyond the measured workload remain deployment-owned.

To roll back application policy, deploy the preceding API image while retaining the Collector volume and compatible backend schemas. To roll back Collector configuration, stop it gracefully, preserve `collector-storage`, restore the preceding validated configuration, and restart. Never delete telemetry/backend volumes as a rollback step unless the retention and recovery owner explicitly accepts that loss.
