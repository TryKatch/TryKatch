# 0004: Vendor-neutral OpenTelemetry pipeline

- Status: Accepted
- Date: 2026-09-07

## Decision

Emit OTLP to an OpenTelemetry Collector, route logs to Loki, traces to Tempo, and metrics to Prometheus, and visualize them in Grafana. Do not deploy Jaeger by default.

## Consequences

Aspire Dashboard remains a local-development tool. Production observability uses separately deployed open-source containers.

