#!/usr/bin/env bash
set -euo pipefail

application_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
configuration="$application_root/web/apps/web/nginx.conf"
container_file="$application_root/web/apps/web/Dockerfile"
compose_file="$application_root/compose.yml"

fail() {
  printf 'Proxy header contract failed: %s\n' "$1" >&2
  exit 1
}

grep -Fq 'set_real_ip_from ${TRYKATCH_INGRESS_PROXY_IP};' "$configuration" ||
  fail 'the immediate ingress proxy is not explicitly trusted'
grep -Fq 'geo $realip_remote_addr $trykatch_trusted_ingress {' "$configuration" ||
  fail 'forwarded scheme handling is not restricted to the trusted ingress peer'
grep -Fq 'proxy_set_header X-Forwarded-For $remote_addr;' "$configuration" ||
  fail 'the client address is not replaced with the address resolved by Nginx'
grep -Fq 'proxy_set_header X-Forwarded-Proto $trykatch_forwarded_proto;' "$configuration" ||
  fail 'the forwarded scheme is not derived through the trusted-ingress policy'
grep -Fq 'NGINX_ENVSUBST_FILTER=^TRYKATCH_INGRESS_PROXY_IP$' "$container_file" ||
  fail 'Nginx template expansion is not restricted to the ingress configuration variable'
grep -Fq 'COPY web/apps/web/nginx.conf /etc/nginx/templates/default.conf.template' "$container_file" ||
  fail 'the Nginx policy is not rendered from its configuration template'
grep -Fq 'TRYKATCH_INGRESS_PROXY_IP: ${TRYKATCH_INGRESS_PROXY_IP:-172.30.250.1}' "$compose_file" ||
  fail 'the trusted ingress peer is not supplied to the web container'
grep -Fq 'gateway: ${TRYKATCH_INGRESS_PROXY_IP:-172.30.250.1}' "$compose_file" ||
  fail 'the trusted same-host ingress address and Compose gateway can diverge'
grep -Fq '/etc/nginx/conf.d:mode=0770,uid=101,gid=101' "$compose_file" ||
  fail 'the read-only web container has no private writable destination for rendered Nginx configuration'

if grep -Eq 'proxy_set_header[[:space:]]+X-Forwarded-(For|Proto)[[:space:]]+\$http_x_forwarded_' "$configuration"; then
  fail 'an untrusted inbound forwarding header is relayed directly to the API'
fi

printf 'Proxy header contract passed.\n'

if [[ ${1:-} != --runtime ]]; then
  exit 0
fi

for command in docker grep; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

runtime_suffix="${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}-$$"
runtime_suffix=$(printf '%s' "$runtime_suffix" | tr -cd '[:alnum:]-' | cut -c1-40)
network_name="trykatch-proxy-$runtime_suffix"
proxy_name="trykatch-proxy-web-$runtime_suffix"
upstream_name="trykatch-proxy-api-$runtime_suffix"
container_image='nginxinc/nginx-unprivileged:1.29.1-alpine3.22@sha256:27985295bdb22a1ef8f712863210bd5877c0f3006494a593e86b3fe0fa55467e'
network_created=false
proxy_created=false
upstream_created=false

cleanup() {
  if [[ $proxy_created == true ]]; then
    docker rm --force "$proxy_name" >/dev/null 2>&1 || true
  fi
  if [[ $upstream_created == true ]]; then
    docker rm --force "$upstream_name" >/dev/null 2>&1 || true
  fi
  if [[ $network_created == true ]]; then
    docker network rm "$network_name" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

docker network create \
  --driver bridge \
  --subnet 172.31.251.0/24 \
  --gateway 172.31.251.1 \
  "$network_name" >/dev/null
network_created=true

docker run --detach \
  --name "$upstream_name" \
  --network "$network_name" \
  --network-alias api \
  --ip 172.31.251.20 \
  --read-only \
  --tmpfs /tmp \
  --tmpfs /var/cache/nginx \
  --tmpfs /var/run \
  --mount "type=bind,source=$application_root/web/apps/web/proxy-test/echo-nginx.conf,target=/etc/nginx/conf.d/default.conf,readonly" \
  "$container_image" >/dev/null
upstream_created=true

docker run --detach \
  --name "$proxy_name" \
  --network "$network_name" \
  --network-alias web \
  --ip 172.31.251.10 \
  --env NGINX_ENVSUBST_FILTER='^TRYKATCH_INGRESS_PROXY_IP$' \
  --env TRYKATCH_INGRESS_PROXY_IP=172.31.251.30 \
  --read-only \
  --tmpfs /tmp \
  --tmpfs /var/cache/nginx \
  --tmpfs /var/run \
  --tmpfs /etc/nginx/conf.d:mode=0770,uid=101,gid=101 \
  --mount "type=bind,source=$configuration,target=/etc/nginx/templates/default.conf.template,readonly" \
  "$container_image" >/dev/null
proxy_created=true

for _ in $(seq 1 20); do
  if docker exec "$proxy_name" wget -q --spider http://127.0.0.1:8080/healthz; then
    break
  fi
  sleep 0.25
done
if ! docker exec "$proxy_name" wget -q --spider http://127.0.0.1:8080/healthz; then
  docker logs "$proxy_name" >&2 || true
  fail 'the rendered Nginx proxy did not become ready'
fi

request_from() {
  local source_ip=$1
  docker run --rm \
    --network "$network_name" \
    --ip "$source_ip" \
    --entrypoint wget \
    "$container_image" \
    -qO- \
    --header='X-Forwarded-For: 198.51.100.99, 203.0.113.10' \
    --header='X-Forwarded-Proto: https' \
    http://web:8080/api/proxy-contract
}

trusted_result=$(request_from 172.31.251.30)
[[ $trusted_result == '203.0.113.10|https' ]] ||
  fail "trusted ingress produced unexpected upstream headers: $trusted_result"

untrusted_result=$(request_from 172.31.251.31)
[[ $untrusted_result == '172.31.251.31|http' ]] ||
  fail "untrusted peer influenced upstream headers: $untrusted_result"

printf 'Proxy header runtime behavior passed.\n'
