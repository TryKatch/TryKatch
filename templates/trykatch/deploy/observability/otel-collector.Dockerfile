FROM alpine:3.23@sha256:fd791d74b68913cbb027c6546007b3f0d3bc45125f797758156952bc2d6daf40 AS storage

RUN mkdir -p /otelcol/storage /otelcol/compaction \
    && touch /otelcol/storage/.keep /otelcol/compaction/.keep

FROM otel/opentelemetry-collector-contrib:0.160.0@sha256:799dc6cf12c96192af37b5bdba804da8c10b3bc563b43cb90c3f3c58d9572ad6

COPY --from=storage --chown=10001:10001 /otelcol /var/lib/otelcol
