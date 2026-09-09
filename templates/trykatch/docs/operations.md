# Operations

Use `dotnet run --project src/TrykatchApp.AppHost` for local orchestration. Aspire starts PostgreSQL, the API, React/Vite, OpenTelemetry Collector, Prometheus, Loki, Tempo, and Grafana. AppHost is development and test orchestration only; it is not a production runtime.

Production runs separate API and web containers. The unprivileged web proxy owns the public origin and forwards `/api` and `/connect` to the API, keeping authentication cookies same-origin. Containers run without root or Linux capabilities, support read-only root filesystems, and expose explicit health checks.

Provide all secrets through environment variables or a mounted secret provider. Never commit `.env`, signing certificates, database passwords, SMTP credentials, or object-storage keys. Container references retain a readable version tag and a reviewed immutable manifest digest; update both together through a reviewed dependency change.

Set `Application__PublicUrl` (or `TRYKATCH_PUBLIC_URL` in Compose) to the externally reachable HTTPS origin. Password-reset and organization-invitation links are built from that trusted value in production. With `--email`, configure the `Email__Host`, `Email__Port`, `Email__From`, and optional SMTP credential settings; invitation creation reports whether delivery succeeded and always returns the same one-time link so an administrator can copy it when SMTP is unavailable.

Before starting Production Compose, copy `.env.example` to `.env`, replace every placeholder, and place separate PKCS#12 signing and encryption certificates at `${TRYKATCH_SECRETS_PATH}/signing.pfx` and `${TRYKATCH_SECRETS_PATH}/encryption.pfx`. The API mounts those files read-only. Keep the certificate passwords in the deployment secret store, rotate certificates through a planned key-overlap window, and back up Data Protection keys with the database. Supply the bootstrap administrator email and password for the first controlled start only, verify the account, and then remove both values.

Telemetry flows once: Serilog writes JSON to stdout and exports logs through OTLP; the OpenTelemetry SDK exports HTTP, runtime, native Npgsql, and outbox traces and metrics. The Collector batches and retries, sends logs to Loki, traces to Tempo, and exposes metrics for Prometheus. Grafana data sources and the starter overview dashboard are provisioned from source with request success, HTTP latency, PostgreSQL latency, and outbox dispatch panels. Jaeger is a supported alternative to Tempo, not an additional default service.

The built-in outbox transport is deliberately broker-free and emits one structured publication event without logging payload data. When a deployment needs external integration events, register an `IOutboxTransport` adapter before `AddInfrastructure`. Preserve `OutboxEnvelope.MessageId` through the transport and deduplicate downstream effects with that identifier because delivery is at least once.
