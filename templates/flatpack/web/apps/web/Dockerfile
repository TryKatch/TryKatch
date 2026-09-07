FROM node:24.7.0-alpine3.22 AS build
RUN corepack enable
WORKDIR /workspace
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
COPY web/apps/web/package.json apps/web/package.json
COPY web/packages/ui/package.json packages/ui/package.json
COPY web/packages/api-client/package.json packages/api-client/package.json
RUN pnpm install --frozen-lockfile
COPY web .
RUN pnpm --filter @flatpackapp/web build

FROM nginxinc/nginx-unprivileged:1.29.1-alpine3.22 AS final
COPY --from=build /workspace/apps/web/dist /usr/share/nginx/html
COPY web/apps/web/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 CMD ["wget", "-q", "--spider", "http://localhost:8080/healthz"]
