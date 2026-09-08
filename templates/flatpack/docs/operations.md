# Operations

Use `dotnet run --project src/FlatpackApp.AppHost` for local orchestration. Aspire starts PostgreSQL, the API, React/Vite, OpenTelemetry Collector, Prometheus, Loki, Tempo, and Grafana. AppHost is development and test orchestration only; it is not a production runtime.

Production runs separate API and web containers. The unprivileged web proxy owns the public origin and forwards `/api` and `/connect` to the API, keeping authentication cookies same-origin. Containers run without root or Linux capabilities, support read-only root filesystems, and expose explicit health checks.

Provide all secrets through environment variables or a mounted secret provider. Never commit `.env`, signing certificates, database passwords, SMTP credentials, or object-storage keys. Replace version tags with reviewed image digests in a production deployment.

Telemetry flows once: Serilog writes JSON to stdout and exports logs through OTLP; the OpenTelemetry SDK exports HTTP, runtime, native Npgsql, and outbox traces and metrics. The Collector batches and retries, sends logs to Loki, traces to Tempo, and exposes metrics for Prometheus. Grafana data sources and the starter overview dashboard are provisioned from source with request success, HTTP latency, PostgreSQL latency, and outbox dispatch panels. Jaeger is a supported alternative to Tempo, not an additional default service.
