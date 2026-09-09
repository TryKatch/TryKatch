---
title: Observability and local orchestration
description: Run the application and understand how logs, traces, and metrics reach the Grafana stack.
---

## Local development

The Aspire AppHost orchestrates PostgreSQL, the API, React/Vite, OpenTelemetry Collector, Prometheus, Loki, Tempo, and Grafana:

```bash
dotnet run --project src/Horizon.AppHost
```

AppHost is a development and test orchestrator. Production runs separate API and web containers behind a same-origin reverse proxy.

## Telemetry flow

- Serilog is the structured `ILogger` provider and emits JSON console logs.
- The OpenTelemetry SDK instruments HTTP, runtime, Npgsql, and outbox activity.
- The Collector batches, retries, and routes signals.
- Loki stores logs, Tempo stores traces, and Prometheus scrapes metrics.
- Grafana provisions data sources and starter dashboards from source control.

Trykatch does not run Jaeger beside Tempo. Jaeger is documented only as an alternative trace backend.

## Health endpoints

- `/health/live` proves the process is alive.
- `/health/ready` proves required runtime dependencies are ready.

Production ingress must protect operational endpoints according to the deployment environment.
