#!/usr/bin/env bash
set -euo pipefail

application_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
configuration="$application_root/web/apps/web/nginx.conf"
container_file="$application_root/web/apps/web/Dockerfile"
compose_file="$application_root/compose.yml"
environment_file="$application_root/.env.example"

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
grep -Fq 'TRYKATCH_INGRESS_PROXY_IP: ${TRYKATCH_INGRESS_PROXY_IP:-172.30.250.2}' "$compose_file" ||
  fail 'the trusted ingress peer is not supplied to the web container'
grep -Fq 'gateway: ${TRYKATCH_NETWORK_GATEWAY:-172.30.250.1}' "$compose_file" ||
  fail 'the Compose gateway is not configured independently from the trusted ingress peer'
grep -Fq 'ip_range: ${TRYKATCH_NETWORK_DYNAMIC_RANGE:-172.30.250.128/25}' "$compose_file" ||
  fail 'automatic container allocation is not isolated from reserved proxy addresses'
grep -Fq 'TRYKATCH_NETWORK_GATEWAY=172.30.250.1' "$environment_file" ||
  fail 'the example environment does not reserve a distinct Compose gateway'
grep -Fq 'TRYKATCH_NETWORK_DYNAMIC_RANGE=172.30.250.128/25' "$environment_file" ||
  fail 'the example environment does not define a safe automatic allocation range'
grep -Fq 'TRYKATCH_INGRESS_PROXY_IP=172.30.250.2' "$environment_file" ||
  fail 'the example environment does not reserve a dedicated ingress address'
grep -Fq '/etc/nginx/conf.d:mode=0770,uid=101,gid=101' "$compose_file" ||
  fail 'the read-only web container has no private writable destination for rendered Nginx configuration'
grep -Fq 'log_format trykatch_safe escape=json' "$configuration" ||
  fail 'Nginx does not define the credential-safe access log format'
grep -Fq 'access_log /dev/stdout trykatch_safe;' "$configuration" ||
  fail 'Nginx does not use the credential-safe access log format'
grep -Fq 'error_log /dev/null;' "$configuration" ||
  fail 'Nginx request error logging is not discarded before it can emit credential-bearing targets'

safe_log_definition=$(grep -F 'log_format trykatch_safe escape=json' "$configuration")
if grep -Eq '\$(request_uri|uri|args|query_string|http_referer|http_cookie|http_authorization)' <<<"$safe_log_definition"; then
  fail 'the safe access log includes request-controlled paths, query values, referrers, cookies, or authorization'
fi

if grep -Eq 'proxy_set_header[[:space:]]+X-Forwarded-(For|Proto)[[:space:]]+\$http_x_forwarded_' "$configuration"; then
  fail 'an untrusted inbound forwarding header is relayed directly to the API'
fi

printf 'Proxy header contract passed.\n'

if [[ ${1:-} != --runtime ]]; then
  exit 0
fi

for command in awk curl docker grep head; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

runtime_suffix="${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}-$$"
runtime_suffix=$(printf '%s' "$runtime_suffix" | tr -cd '[:alnum:]-' | cut -c1-40)
network_name="trykatch-proxy-$runtime_suffix"
proxy_name="trykatch-proxy-web-$runtime_suffix"
upstream_name="trykatch-proxy-api-$runtime_suffix"
stale_name="trykatch-proxy-api-stale-$runtime_suffix"
replacement_name="trykatch-proxy-api-replacement-$runtime_suffix"
container_image='nginxinc/nginx-unprivileged:1.29.1-alpine3.22@sha256:27985295bdb22a1ef8f712863210bd5877c0f3006494a593e86b3fe0fa55467e'
network_created=false
proxy_created=false
upstream_created=false
stale_created=false
replacement_created=false

cleanup() {
  if [[ $proxy_created == true ]]; then
    docker rm --force "$proxy_name" >/dev/null 2>&1 || true
  fi
  if [[ $upstream_created == true ]]; then
    docker rm --force "$upstream_name" >/dev/null 2>&1 || true
  fi
  if [[ $replacement_created == true ]]; then
    docker rm --force "$replacement_name" >/dev/null 2>&1 || true
  fi
  if [[ $stale_created == true ]]; then
    docker rm --force "$stale_name" >/dev/null 2>&1 || true
  fi
  if [[ $network_created == true ]]; then
    docker network rm "$network_name" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

docker network create \
  --driver bridge \
  --subnet 172.31.251.0/24 \
  --ip-range 172.31.251.128/25 \
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
  --publish 127.0.0.1::8080 \
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
  local source_ip=$1 url=${2:-http://web:8080/api/proxy-contract}
  docker run --rm \
    --network "$network_name" \
    --ip "$source_ip" \
    --entrypoint wget \
    "$container_image" \
    -qO- \
    --header='X-Forwarded-For: 198.51.100.99, 203.0.113.10' \
    --header='X-Forwarded-Proto: https' \
    "$url"
}

request_from_dynamic_peer() {
  local url=${1:-http://web:8080/api/proxy-contract}
  docker run --rm \
    --network "$network_name" \
    --entrypoint wget \
    "$container_image" \
    -qO- \
    --header='X-Forwarded-For: 198.51.100.99, 203.0.113.10' \
    --header='X-Forwarded-Proto: https' \
    "$url"
}

trusted_result=$(request_from 172.31.251.30)
[[ $trusted_result == '203.0.113.10|https' ]] ||
  fail "trusted ingress produced unexpected upstream headers: $trusted_result"

untrusted_result=$(request_from 172.31.251.31)
[[ $untrusted_result == '172.31.251.31|http' ]] ||
  fail "untrusted peer influenced upstream headers: $untrusted_result"

dynamic_result=$(request_from_dynamic_peer)
dynamic_address=${dynamic_result%%|*}
dynamic_scheme=${dynamic_result##*|}
[[ $dynamic_address == 172.31.251.* && $dynamic_address != 172.31.251.30 && $dynamic_scheme == http ]] ||
  fail "an automatically allocated peer received ingress trust: $dynamic_result"

published_port=$(docker port "$proxy_name" 8080/tcp | awk -F: 'NR == 1 { print $NF }')
[[ -n $published_port ]] || fail 'the host-published proxy port could not be resolved'
host_result=$(curl --connect-timeout 2 --max-time 5 --fail --silent --show-error \
  --header 'X-Forwarded-For: 198.51.100.99, 203.0.113.10' \
  --header 'X-Forwarded-Proto: https' \
  "http://127.0.0.1:$published_port/api/proxy-contract")
host_address=${host_result%%|*}
host_scheme=${host_result##*|}
[[ $host_address != 198.51.100.99 && $host_address != 203.0.113.10 && $host_scheme == http ]] ||
  fail "a host-published request influenced upstream headers: $host_result"

path_sentinel='reset-path-secret-9472'
query_sentinel='query-secret-5831'
curl --connect-timeout 2 --max-time 5 --fail --silent --show-error \
  "http://127.0.0.1:$published_port/api/v1/auth/password/reset/$path_sentinel?token=$query_sentinel" >/dev/null
proxy_logs=$(docker logs "$proxy_name" 2>&1)
if grep -Fq "$path_sentinel" <<<"$proxy_logs" || grep -Fq "$query_sentinel" <<<"$proxy_logs"; then
  fail 'Nginx access logs contain a credential-bearing request path or query value'
fi
grep -Fq '"route":"api"' <<<"$proxy_logs" ||
  fail 'Nginx access logs do not include the safe API route label'

docker exec --user root "$proxy_name" sh -c \
  'mkdir -p /tmp/client_temp && chmod 000 /tmp/client_temp'
critical_path_sentinel='critical-path-secret-6248'
critical_query_sentinel='critical-query-secret-1937'
critical_status=$(head -c 20000 /dev/zero | curl --connect-timeout 2 --max-time 5 \
  --silent --output /dev/null --write-out '%{http_code}' --data-binary @- \
  "http://127.0.0.1:$published_port/api/v1/auth/password/reset/$critical_path_sentinel?token=$critical_query_sentinel")
[[ $critical_status == 500 ]] || fail "an unwritable request-body store returned unexpected HTTP status $critical_status"
proxy_logs=$(docker logs "$proxy_name" 2>&1)
if grep -Fq "$critical_path_sentinel" <<<"$proxy_logs" || grep -Fq "$critical_query_sentinel" <<<"$proxy_logs"; then
  fail 'Nginx logs contain a credential-bearing target after a critical request failure'
fi

web_container_id=$(docker inspect --format '{{.Id}}' "$proxy_name")
initial_dns_url='http://web:8080/api/proxy-dns-contract?phase=initial&token=dns-probe-1652'
initial_dns_result=$(request_from 172.31.251.30 "$initial_dns_url")
[[ $initial_dns_result == '172.31.251.20|203.0.113.10|https|/api/proxy-dns-contract?phase=initial&token=dns-probe-1652' ]] ||
  fail "the initial API proxy did not preserve destination, trusted headers, path and query: $initial_dns_result"

docker stop "$upstream_name" >/dev/null
docker rm "$upstream_name" >/dev/null
upstream_created=false

docker run --detach \
  --name "$stale_name" \
  --network "$network_name" \
  --ip 172.31.251.20 \
  --read-only \
  --tmpfs /tmp \
  --tmpfs /var/cache/nginx \
  --tmpfs /var/run \
  --mount "type=bind,source=$application_root/web/apps/web/proxy-test/stale-nginx.conf,target=/etc/nginx/conf.d/default.conf,readonly" \
  "$container_image" >/dev/null
stale_created=true

docker run --detach \
  --name "$replacement_name" \
  --network "$network_name" \
  --network-alias api \
  --ip 172.31.251.21 \
  --read-only \
  --tmpfs /tmp \
  --tmpfs /var/cache/nginx \
  --tmpfs /var/run \
  --mount "type=bind,source=$application_root/web/apps/web/proxy-test/echo-nginx.conf,target=/etc/nginx/conf.d/default.conf,readonly" \
  "$container_image" >/dev/null
replacement_created=true

replacement_url='http://web:8080/api/proxy-dns-contract?phase=recreated&token=dns-probe-2471'
expected_replacement='172.31.251.21|203.0.113.10|https|/api/proxy-dns-contract?phase=recreated&token=dns-probe-2471'
replacement_result=''
for _ in $(seq 1 30); do
  replacement_result=$(request_from 172.31.251.30 "$replacement_url" 2>/dev/null) || true
  if [[ $replacement_result == "$expected_replacement" ]]; then
    break
  fi
  sleep 0.5
done
[[ $replacement_result == "$expected_replacement" ]] ||
  fail "the same web container did not refresh the moved API destination while preserving headers, path and query: $replacement_result"
[[ $(docker inspect --format '{{.Id}}' "$proxy_name") == "$web_container_id" ]] ||
  fail 'the web container changed during upstream recovery'

docker rm --force "$stale_name" >/dev/null
stale_created=false
docker run --detach \
  --name "$stale_name" \
  --network "$network_name" \
  --network-alias api \
  --ip 172.31.251.20 \
  --read-only \
  --tmpfs /tmp \
  --tmpfs /var/cache/nginx \
  --tmpfs /var/run \
  --mount "type=bind,source=$application_root/web/apps/web/proxy-test/stale-nginx.conf,target=/etc/nginx/conf.d/default.conf,readonly" \
  "$container_image" >/dev/null
stale_created=true

dns_records=''
for _ in $(seq 1 20); do
  dns_records=$(docker exec "$proxy_name" nslookup api. 127.0.0.11 2>/dev/null || true)
  if grep -Fq '172.31.251.20' <<<"$dns_records" && grep -Fq '172.31.251.21' <<<"$dns_records"; then
    break
  fi
  sleep 0.5
done
if ! grep -Fq '172.31.251.20' <<<"$dns_records" || ! grep -Fq '172.31.251.21' <<<"$dns_records"; then
  stale_network=$(docker inspect --format '{{range $name, $network := .NetworkSettings.Networks}}{{printf "%s ip=%s aliases=%v" $name $network.IPAddress $network.Aliases}}{{end}}' "$stale_name" 2>&1 || true)
  replacement_network=$(docker inspect --format '{{range $name, $network := .NetworkSettings.Networks}}{{printf "%s ip=%s aliases=%v" $name $network.IPAddress $network.Aliases}}{{end}}' "$replacement_name" 2>&1 || true)
  fail "the runtime DNS fixture did not expose both upstream peers for the non-idempotent retry check; answer=${dns_records//$'\n'/; }; stale=$stale_network; replacement=$replacement_network"
fi
stale_network=$(docker inspect --format '{{range $name, $network := .NetworkSettings.Networks}}{{printf "%s ip=%s aliases=%v" $name $network.IPAddress $network.Aliases}}{{end}}' "$stale_name")
replacement_network=$(docker inspect --format '{{range $name, $network := .NetworkSettings.Networks}}{{printf "%s ip=%s aliases=%v" $name $network.IPAddress $network.Aliases}}{{end}}' "$replacement_name")
printf 'API DNS peers: %s\n' "${dns_records//$'\n'/; }"
printf 'Stale probe endpoint: %s\nReplacement endpoint: %s\n' "$stale_network" "$replacement_network"

# Docker DNS readiness is not nginx upstream readiness. Observe both real peers
# through the proxy before testing affinity against the now-stable topology.
# Distinct trusted client prefixes exercise ip_hash without changing its policy.
proxy_peers=''
for _ in $(seq 1 30); do
  proxy_peers=$(docker run --rm \
    --network "$network_name" \
    --ip 172.31.251.30 \
    --entrypoint sh \
    "$container_image" -ec '
      for prefix in 1 2 3 4 5 6 7 8; do
        wget -T 2 -qO- \
          --header="X-Forwarded-For: 203.0.$prefix.10" \
          --header="X-Forwarded-Proto: https" \
          http://web:8080/api/proxy-affinity-contract
      done
    ' | awk '!seen[$0]++') || true
  if grep -Fxq '172.31.251.20' <<<"$proxy_peers" && grep -Fxq '172.31.251.21' <<<"$proxy_peers"; then
    break
  fi
  sleep 0.5
done
if ! grep -Fxq '172.31.251.20' <<<"$proxy_peers" || ! grep -Fxq '172.31.251.21' <<<"$proxy_peers"; then
  fail "the proxy did not serve both API peers before the affinity check: ${proxy_peers//$'\n'/; }"
fi
printf 'Proxy-resolved API peers: %s\n' "${proxy_peers//$'\n'/; }"

affinity_peer=''
for _ in $(seq 1 12); do
  observed_peer=$(curl --connect-timeout 2 --max-time 5 --fail --silent \
    "http://127.0.0.1:$published_port/api/proxy-affinity-contract")
  [[ $observed_peer == 172.31.251.20 || $observed_peer == 172.31.251.21 ]] || \
    fail "the affinity probe did not reach one of the two actual API peers: $observed_peer"
  if [[ -z $affinity_peer ]]; then affinity_peer=$observed_peer; fi
  [[ $observed_peer == "$affinity_peer" ]] || \
    fail "one client changed API peers between requests ($affinity_peer -> $observed_peer); SignalR negotiation and upgrade require affinity"
done

post_path_sentinel='reset-path-secret-7284'
post_query_sentinel='query-secret-3159'
post_body_sentinel='token-body-secret-8461'
post_status=$(curl --connect-timeout 2 --max-time 5 --silent --output /dev/null --write-out '%{http_code}' \
  --data-binary "$post_body_sentinel" \
  "http://127.0.0.1:$published_port/api/v1/auth/password/reset/dns-replay-probe/$post_path_sentinel?token=$post_query_sentinel")
[[ $post_status == 502 ]] || fail "the sensitive POST probe returned unexpected HTTP status $post_status"
replacement_logs=$(docker logs "$replacement_name" 2>&1)
stale_logs=$(docker logs "$stale_name" 2>&1)
post_attempts=$(printf '%s\n%s\n' "$replacement_logs" "$stale_logs" | awk '$1 == "POST" && $2 == "444" { count++ } END { print count + 0 }')
[[ $post_attempts == 1 ]] || fail "the failed sensitive POST reached upstream $post_attempts times instead of once"
proxy_logs=$(docker logs "$proxy_name" 2>&1)
if grep -Fq "$post_path_sentinel" <<<"$proxy_logs$replacement_logs$stale_logs" || \
  grep -Fq "$post_query_sentinel" <<<"$proxy_logs$replacement_logs$stale_logs" || \
  grep -Fq "$post_body_sentinel" <<<"$proxy_logs$replacement_logs$stale_logs"; then
  fail 'proxy or upstream logs contain a sensitive POST path, query or body'
fi

docker stop "$replacement_name" >/dev/null
docker stop "$stale_name" >/dev/null
failure_path_sentinel='failed-path-secret-3619'
failure_query_sentinel='failed-query-secret-7154'
failure_status=$(curl --connect-timeout 2 --max-time 5 --silent --output /dev/null --write-out '%{http_code}' \
  "http://127.0.0.1:$published_port/api/v1/auth/password/reset/$failure_path_sentinel?token=$failure_query_sentinel")
[[ $failure_status == 502 || $failure_status == 504 ]] ||
  fail "an unavailable upstream returned unexpected HTTP status $failure_status"
proxy_logs=$(docker logs "$proxy_name" 2>&1)
if grep -Fq "$failure_path_sentinel" <<<"$proxy_logs" || grep -Fq "$failure_query_sentinel" <<<"$proxy_logs"; then
  fail 'Nginx logs contain a credential-bearing target after an upstream failure'
fi

printf 'Proxy header runtime behavior passed.\n'
