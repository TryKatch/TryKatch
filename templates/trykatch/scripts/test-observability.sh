#!/usr/bin/env bash
set -euo pipefail

static_only=false
if [[ ${1:-} == --static ]]; then
  static_only=true
  template_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
else
  template_root=${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}
fi
template_root=$(cd "$template_root" && pwd -P)
validation_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-observability.XXXXXX")
trap 'rm -rf "$validation_root"' EXIT

export TRYKATCH_RELEASE_VERSION=ci-validation
docker compose --env-file "$template_root/.env.example" -f "$template_root/compose.yml" config --quiet
docker compose --env-file "$template_root/.env.example" -f "$template_root/compose.backend.yml" config --quiet
docker compose --env-file "$template_root/.env.example" -f "$template_root/compose.yml" -f "$template_root/compose.observability-tls.yml" config --quiet
jq empty "$template_root/deploy/observability/grafana/dashboards/json/trykatch-overview.json"

grep -Fq 'processors: [memory_limiter, transform/privacy, redaction/privacy, batch]' "$template_root/deploy/observability/otel-collector.yml"
grep -Fq 'storage: file_storage' "$template_root/deploy/observability/otel-collector.yml"
grep -Fq 'port: 8888' "$template_root/deploy/observability/otel-collector.yml"
grep -Fq 'retention_enabled: true' "$template_root/deploy/observability/loki.yml"
grep -Fq 'retention_period: 168h' "$template_root/deploy/observability/loki.yml"
[[ $(grep -Fc 'block_retention: 24h' "$template_root/deploy/observability/tempo.yml") -eq 2 ]]
grep -Fq -- '--storage.tsdb.retention.time=15d' "$template_root/compose.yml"
grep -Fq '127.0.0.1}:3000:3000' "$template_root/compose.yml"

telemetry_loss_expression='(sum(increase(otelcol_receiver_refused_log_records_total[5m])) or vector(0)) + (sum(increase(otelcol_receiver_refused_spans_total[5m])) or vector(0)) + (sum(increase(otelcol_exporter_send_failed_log_records_total[5m])) or vector(0)) + (sum(increase(otelcol_exporter_send_failed_spans_total[5m])) or vector(0))'
grep -Fq "expr: '$telemetry_loss_expression'" "$template_root/deploy/observability/grafana/alerting/rules.yml"

if [[ $static_only == true ]]; then
  exit 0
fi

collector_image='otel/opentelemetry-collector-contrib:0.160.0@sha256:799dc6cf12c96192af37b5bdba804da8c10b3bc563b43cb90c3f3c58d9572ad6'
prometheus_image='prom/prometheus:v3.14.0@sha256:5ce7540c3c00ef4ab0c9d2c995c6a5b9c421f44b4a115d97a2c7af3b1c21cbb0'
loki_image='grafana/loki:3.7.2@sha256:191d4fdfb7264f16989f0a57f320872620a5a7c2ceeec6229212c4190ec49b86'
tempo_image='grafana/tempo:3.0.2@sha256:cda87c212d8c584dc0b89e337e7ed648a5100feb657e5d528480ee4fa03dbbe3'

docker run --rm -v "$template_root/deploy/observability/otel-collector.yml:/etc/otelcol-contrib/config.yaml:ro" \
  "$collector_image" validate --config=/etc/otelcol-contrib/config.yaml
docker run --rm --entrypoint /bin/promtool \
  -v "$template_root/deploy/observability/prometheus.yml:/etc/prometheus/prometheus.yml:ro" \
  "$prometheus_image" check config /etc/prometheus/prometheus.yml
docker run --rm -v "$template_root/deploy/observability/loki.yml:/etc/loki/local-config.yaml:ro" \
  "$loki_image" -config.file=/etc/loki/local-config.yaml -verify-config=true
docker run --rm -v "$template_root/deploy/observability/tempo.yml:/etc/tempo.yml:ro" \
  "$tempo_image" -config.file=/etc/tempo.yml -config.verify

openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=otel-collector' \
  -addext 'subjectAltName=DNS:otel-collector' -keyout "$validation_root/server.key" \
  -out "$validation_root/server.crt" >/dev/null 2>&1
printf '%s\n' 'ci-only-token' > "$validation_root/token"
docker run --rm \
  -v "$template_root/deploy/observability/otel-collector.tls.yml:/etc/otelcol-contrib/config.yaml:ro" \
  -v "$validation_root/server.crt:/run/secrets/otel-server.crt:ro" \
  -v "$validation_root/server.key:/run/secrets/otel-server.key:ro" \
  -v "$validation_root/token:/run/secrets/otel-token:ro" \
  "$collector_image" validate --config=/etc/otelcol-contrib/config.yaml
