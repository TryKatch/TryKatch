FROM node:24.7.0-alpine3.22@sha256:be4d5e92ac68483ec71440bf5934865b4b7fcb93588f17a24d411d15f0204e4f AS build
RUN corepack enable
WORKDIR /workspace
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN pnpm fetch --frozen-lockfile
COPY web .
RUN pnpm install --offline --frozen-lockfile
RUN pnpm --filter @flatpackapp/web build

FROM nginxinc/nginx-unprivileged:1.29.1-alpine3.22@sha256:27985295bdb22a1ef8f712863210bd5877c0f3006494a593e86b3fe0fa55467e AS final
COPY --from=build /workspace/apps/web/dist /usr/share/nginx/html
COPY web/apps/web/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 CMD ["wget", "-q", "--spider", "http://localhost:8080/healthz"]
