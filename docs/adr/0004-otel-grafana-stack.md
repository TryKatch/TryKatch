# 0004: Vendor-neutral OpenTelemetry pipeline

- Status: Accepted
- Date: 2026-09-07

## Decision

Emit OTLP to an OpenTelemetry Collector, route logs to Loki, traces to Tempo, and metrics to Prometheus, and visualize them in Grafana. Do not deploy Jaeger by default.

ServiceDefaults is the application observability seam: it resolves one resource/transport/sampling/privacy policy for the OpenTelemetry SDK and the sole Serilog `ILogger` pipeline. Compose owns production transport, queues, retention, dashboards, and alerts. Plaintext is supported only on the controlled private single-host network; authenticated TLS is required across trust domains.

## Consequences

AppHost run mode/DCP and Aspire Dashboard remain local-development and test tools. AppHost publish mode can generate deployment input, but this repository has no production publisher and does not claim artifact equivalence. Checked-in Compose remains the deployment authority until another adapter is qualified in staging. Persistent queues reduce restart loss for accepted logs/traces but do not provide exactly-once delivery or survive host/storage loss; trace head sampling does not guarantee complete error traces.
