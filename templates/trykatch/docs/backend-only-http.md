# Backend-only HTTP walkthrough

[Français](backend-only-http.fr.md) · [First feature](first-feature.md)

No generated API client is installed in `--ui none` output. Use this explicit cookie/antiforgery sequence with `curl` and `jq`, installed separately on your laptop. This recipe is for a fresh, disposable **Development** application started through Aspire AppHost, never a deployed system. Obtain the API's HTTPS origin from Aspire and keep AppHost running. Trust the development certificate using your platform's supported configuration; do not use `--insecure` or disable TLS verification.

AppHost enables local demo seeding for both UI modes. `tenant@trykatch.net` / `Admin@123` is the Owner of `Demo Workspace` (`demo-workspace`). `admin@trykatch.net` is a platform administrator, not an implicit organization member. If demo seeding was disabled or customized, use your provisioned account and inspect its actual memberships instead; do not enable demo credentials in production. No AI key, bearer token or password-grant endpoint is needed.

## 1. Prepare a private local session

Open a dedicated Bash shell by running `bash` in a second terminal. Paste the blocks below **in order into that same shell**. `read` prompts for the actual origin, for example the HTTPS API resource in Aspire, not the dashboard or web URL. The loopback-only guard prevents accidentally sending demo credentials to a remote server. Commands fail on HTTP errors; if the shell exits, restart from this step. A private temporary file holds cookies and is removed when this shell exits. Do not log/share that file, tokens, login responses or verbose curl output.

```bash
set -euo pipefail
command -v curl >/dev/null
command -v jq >/dev/null
read -r -p 'API HTTPS origin from Aspire: ' http_api_origin
[[ "$http_api_origin" =~ ^https://localhost:[0-9]+$ ]] || { printf 'Use the local HTTPS API origin, without a path.\n' >&2; exit 2; }
umask 077
http_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/trykatch-http-cookie.XXXXXX")
trap 'rm -f -- "$http_cookie_jar"' EXIT
http_request() {
  local http_path=$1
  shift
  curl --fail --silent --show-error --proto '=https' --max-time 30 \
    --cookie "$http_cookie_jar" --cookie-jar "$http_cookie_jar" \
    "$http_api_origin$http_path" "$@"
}
http_csrf() {
  http_request '/api/v1/auth/antiforgery' | jq -er '.token | select(type == "string" and length > 0)'
}
```

## 2. Log in, refresh antiforgery, select the workspace

The first token is anonymous and allows login. After login, obtain a new token bound to the authenticated identity before workspace selection or any other write. Membership discovery supplies the organization ID; never guess one or put it into Equipment requests. Workspace selection writes the protected context cookie and rechecks membership server-side.

```bash
http_csrf_token=$(http_csrf)
printf '%s' '{"email":"tenant@trykatch.net","password":"Admin@123","rememberMe":false}' |
  http_request '/api/v1/auth/login' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" \
    --data-binary @- >/dev/null
http_csrf_token=$(http_csrf)
http_organizations=$(http_request '/api/v1/me/organizations')
http_organization_id=$(printf '%s' "$http_organizations" |
  jq -er '[.[] | select(.slug == "demo-workspace")] | if length == 1 then .[0].id else error("Expected one demo membership") end')
jq -n --arg id "$http_organization_id" '{organizationId: $id, remember: false}' |
  http_request '/api/v1/workspace/select' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" \
    --data-binary @- >/dev/null
http_request '/api/v1/workspace/current' |
  jq -e --arg id "$http_organization_id" '.organizationId == $id' >/dev/null
http_request '/api/v1/projects/' | jq -e '.items | type == "array"' >/dev/null
printf 'Authenticated workspace and Projects read passed.\n'
```

Checkpoint: login returns 200, workspace selection 204, current workspace and Projects return 200. Projects returns a paged object; an empty `items` array is valid. This completes the startup checkpoint without a frontend. If you are about to stop AppHost to generate Equipment, finish with section 4 now; after restarting AppHost, repeat sections 1–2 in a new Bash shell before section 3.

## 3. Exercise the generated Equipment module

Only after generating the exact module in [first feature](first-feature.md), restarting AppHost and checking live/ready health, run this in the authenticated shell. Confirm `equipment.read` and `equipment.manage` in `/api/v1/workspace/current`; the seeded Owner receives installed organization permissions. Never infer ordinary membership grants from that account. This example creates one disposable record, reads it back, updates it with its current `expectedVersion`, archives and restores it, then leaves it archived. Rates are invariant JSON strings. No organization/actor fields belong in the save payload.

```bash
http_request '/api/v1/workspace/current' |
  jq -e '(.permissions | index("equipment.read") != null) and (.permissions | index("equipment.manage") != null)' >/dev/null
http_csrf_token=$(http_csrf)
http_equipment=$(printf '%s' '{"name":"Training excavator","dailyRate":"125.50"}' |
  http_request '/api/v1/equipment_items/' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @-)
http_equipment_id=$(printf '%s' "$http_equipment" | jq -er '.id')
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '.name == "Training excavator" and (.dailyRate | tonumber) == 125.50' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{name: "Training excavator", dailyRate: "130.00", expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id" --request PUT \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '(.dailyRate | tonumber) == 130.00' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/archive" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request '/api/v1/equipment_items/?lifecycle=recoverable' |
  jq -ec --arg id "$http_equipment_id" '.[] | select(.id == $id and .lifecycle.status == "Archived")')
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/restore" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '.lifecycle.status == "Active"' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/archive" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
printf 'Equipment create/read/update/archive/restore passed; disposable record left archived.\n'
```

Checkpoint: create 201, reads/update 200, lifecycle writes 204. `expectedVersion` is the latest server-issued GUID, not an ETag or a made-up counter. Re-read after changes; a stale version returns 409. Recoverable listing supplies the archived version before restore.

This Owner happy path does not establish denied-write behavior or cross-organization isolation. Continue the separate memberships, validation, atomicity and real PostgreSQL checks in [first feature](first-feature.md). Do not reuse this cookie jar for a different test actor.

## 4. Log out and remove local cookies

```bash
http_csrf_token=$(http_csrf)
http_request '/api/v1/auth/logout' --request POST \
  --header "X-CSRF-TOKEN: $http_csrf_token" >/dev/null
printf 'Logged out; exiting removes the temporary cookie file.\n'
exit
```

Logout returns 204. Exiting removes the one temporary cookie file, not application data. The archived Equipment record remains recoverable through supported lifecycle operations; this recipe never performs a hard delete. Closing a local shell is not a substitute for server logout.

Troubleshooting: TLS errors need certificate trust, not `-k`; 401 needs a valid account/session; 400 during a write can mean missing/stale antiforgery or invalid input; 403 can mean missing workspace/membership/permission. Check current workspace and API resource logs before retrying. If login requests MFA (428), follow the documented `/api/v1/auth/login/mfa` contract instead of bypassing it. Inspect your installed `/docs` for customized routes/schemas.
