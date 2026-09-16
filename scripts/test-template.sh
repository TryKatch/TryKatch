#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/trykatch-template.XXXXXX")
test_root=$(cd "$test_root" && pwd -P)
trap 'rm -rf "$test_root"' EXIT
template_hive="$test_root/template-hive"
template_manifest="$repository_root/templates/trykatch/.template.config/template.json"

fail() {
  printf 'Template contract failed: %s\n' "$1" >&2
  exit 1
}

legacy_brand_pattern='([nN][aA][nN][oO].{0,24}[bB][oO][iI][lL][eE][rR][pP][lL][aA][tT][eE])|([aA][sS][pP].{0,12}[nN][aA][nN][oO])'
if git -C "$repository_root" grep -n -I -E "$legacy_brand_pattern" -- .; then
  fail 'tracked source contains legacy product branding'
fi
grep -Fq '"identity": "Trykatch.Templates.Enterprise"' "$template_manifest" ||
  fail 'the template identity is not owned by Trykatch'
grep -Fq '"groupIdentity": "Trykatch.Templates"' "$template_manifest" ||
  fail 'the template group identity is not owned by Trykatch'
grep -Fq '"author": "Trykatch contributors"' "$template_manifest" ||
  fail 'the template author is not Trykatch contributors'

dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$test_root/package"
package_path=$(find "$test_root/package" -name 'Trykatch.Templates.*.nupkg' -print -quit)
dotnet new --debug:custom-hive "$template_hive" install "$package_path" --force
dotnet pack \
  "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool/Trykatch.ModuleTool.csproj" \
  -c Release \
  -o "$test_root/package" \
  -p:PackageVersion=0.1.0-ci
dotnet tool install \
  Trykatch.Cli \
  --version 0.1.0-ci \
  --tool-path "$test_root/tools" \
  --add-source "$test_root/package"
"$test_root/tools/trykatch" --help >/dev/null
help_output=$("$test_root/tools/trykatch" help)
grep -Fq 'Create an application:' <<<"$help_output" ||
  fail 'CLI help does not explain application creation'
grep -Fq 'trykatch new <name> [options]' <<<"$help_output" ||
  fail 'CLI help does not show the application creation command'
grep -Fq 'trykatch template install' <<<"$help_output" ||
  fail 'CLI help does not show the progress-aware template installer'
grep -Fq 'trykatch template uninstall' <<<"$help_output" ||
  fail 'CLI help does not show template removal'
grep -Fq 'trykatch update' <<<"$help_output" ||
  fail 'CLI help does not show the template update command'
grep -Fq 'trykatch start' <<<"$help_output" ||
  fail 'CLI help does not show the application start command'
grep -Fq -- '--ui <react|none>' <<<"$help_output" ||
  fail 'CLI help does not document the frontend choice'
grep -Fq 'Module lifecycle:' <<<"$help_output" ||
  fail 'CLI help does not document module lifecycle commands'
module_help_output=$("$test_root/tools/trykatch" module help)
grep -Fq 'trykatch module create <name>' <<<"$module_help_output" ||
  fail 'module help does not document source-module creation'
grep -Fq 'trykatch module doctor' <<<"$module_help_output" ||
  fail 'module help does not document workspace validation'
grep -Fq 'trykatch module remove <id>' <<<"$module_help_output" ||
  fail 'module help does not document the remove alias'
nested_help_output=$("$test_root/tools/trykatch" module list --help)
grep -Fq 'Trykatch module lifecycle' <<<"$nested_help_output" ||
  fail 'nested module commands do not support --help'
create_help_output=$("$test_root/tools/trykatch" module create --help)
grep -Fq -- '--ownership organization' <<<"$create_help_output" ||
  fail 'module create help does not require an explicit ownership boundary'
grep -Fq -- '--with-web' <<<"$create_help_output" ||
  fail 'module create help does not document optional React generation'
grep -Fq -- '--fields <contract>' <<<"$create_help_output" ||
  fail 'module create help does not document contract-driven business fields'
template_help_output=$("$test_root/tools/trykatch" template help)
grep -Fq 'trykatch template install [--version <version>] [--force]' <<<"$template_help_output" ||
  fail 'template help does not document installation options'
grep -Fq 'trykatch template update [--version <version>]' <<<"$template_help_output" ||
  fail 'template help does not document update options'
grep -Fq 'trykatch template uninstall' <<<"$template_help_output" ||
  fail 'template help does not document template removal'
start_help_output=$("$test_root/tools/trykatch" start --help)
grep -Fq 'trykatch start [--root <path>]' <<<"$start_help_output" ||
  fail 'start help does not document AppHost discovery'
new_help_output=$("$test_root/tools/trykatch" new --help)
grep -Fq 'trykatch new <name> [options]' <<<"$new_help_output" ||
  fail 'new help does not document application generation'
grep -Fq 'initialized as a Git repository on the main branch' <<<"$new_help_output" ||
  fail 'new help does not explain Git initialization'
grep -Fq 'Live progress shows validation and template/Git setup' <<<"$new_help_output" ||
  fail 'new help does not explain application creation progress'
cli_informational_version=$(dotnet "$(find "$test_root/tools/.store/trykatch.cli/0.1.0-ci" -name 'Trykatch.ModuleTool.dll' -print -quit)" --version 2>/dev/null || true)
test "$cli_informational_version" = 'Trykatch CLI 0.1.0-ci' ||
  fail "packaged CLI reports '$cli_informational_version' instead of its package version"

generate_and_build() {
  local name=$1
  shift
  local namespace_name=${name//-/.}
  local output="$test_root/$namespace_name"
  "$test_root/tools/trykatch" new "$name" --output "$output" "$@" --debug:custom-hive "$template_hive" | tee "$test_root/$namespace_name.creation.log"
  node "$repository_root/scripts/test-development-skills.mjs" "$output" "$namespace_name"
  grep -Fq 'Running application template and packaged Git setup' "$test_root/$namespace_name.creation.log" ||
    fail 'application creation does not report its active phase'
  grep -Fq 'Application generation completed in ' "$test_root/$namespace_name.creation.log" ||
    fail 'application creation does not report completion and elapsed time'
  test -d "$output/.git" || fail "generated application '$name' was not initialized as a Git repository"
  test "$(git -C "$output" branch --show-current)" = main ||
    fail "generated application '$name' did not use main as its initial Git branch"
  grep -Fq '<FrameworkReference Include="Microsoft.AspNetCore.App" />' \
    "$output/tests/Modules/Documents/$namespace_name.Modules.Documents.UnitTests/$namespace_name.Modules.Documents.UnitTests.csproj" ||
    fail "generated Documents unit tests omit the ASP.NET shared framework used by their composition root"
  grep -Fq "src/Common/$namespace_name.Modules.Abstractions/$namespace_name.Modules.Abstractions.csproj" \
    "$output/tests/Modules/Documents/$namespace_name.Modules.Documents.UnitTests/$namespace_name.Modules.Documents.UnitTests.csproj" ||
    fail "generated Documents unit tests omit their directly consumed module contract"
  dotnet test \
    "$output/tests/Modules/Documents/$namespace_name.Modules.Documents.UnitTests/$namespace_name.Modules.Documents.UnitTests.csproj"
  dotnet restore "$output/$namespace_name.slnx"
  dotnet build "$output/$namespace_name.slnx" --no-restore
  dotnet run --project "$output/tools/$namespace_name.ModuleTool" --no-build -- module doctor --root "$output"
  TRYKATCH_RELEASE_VERSION=ci-validation docker compose --env-file "$output/.env.example" -f "$output/compose.yml" config --quiet
  test -f "$output/deploy/observability/otel-collector.tls.yml"
  test -f "$output/compose.observability-tls.yml"
  test -f "$output/compose.identity-maintenance.yml"
  test -f "$output/docs/production-identity.md"
  grep -Fq 'DataProtection__Certificate__PasswordFile: /run/secrets/data-protection.password' "$output/compose.yml" ||
    fail "generated application '$name' is missing production key encryption"
  grep -Fq 'OpenIddict__SigningCertificate__PasswordFile:' "$output/compose.yml" ||
    fail "generated application '$name' is missing certificate password-file configuration"
  if [[ -d "$output/src/Common/$namespace_name.Infrastructure/Optional/Email" ]]; then
    grep -Fq 'services.AddEmailModule(configuration, isDevelopment, isOpenApiGeneration)' "$output/src/Common/$namespace_name.Infrastructure/DependencyInjection.cs" ||
      fail "generated email application '$name' does not wire the SMTP adapter"
    grep -RFq --include='*.cs' 'Email__Security' "$output/src/API/$namespace_name.AppHost" ||
      fail "generated email application '$name' does not wire Mailpit transport security"
    grep -Fq 'RealSmtpDeliveryUsesConfiguredTransport' "$output/tests/$namespace_name.IntegrationTests/SmtpTransportTests.cs" ||
      fail "generated email application '$name' omits its SMTP tests"
    grep -Fq 'Email__Security:' "$output/compose.yml" ||
      fail "generated email application '$name' omits production SMTP configuration"
    dotnet test "$output/tests/$namespace_name.IntegrationTests" --no-build --filter FullyQualifiedName~SmtpTransportTests
  else
    if grep -Fq 'services.AddEmailModule(' "$output/src/Common/$namespace_name.Infrastructure/DependencyInjection.cs" ||
      grep -Fq 'RealSmtpDeliveryUsesConfiguredTransport' "$output/tests/$namespace_name.IntegrationTests/SmtpTransportTests.cs" ||
      grep -Fq 'Email__Security:' "$output/compose.yml"; then
      fail "generated no-email application '$name' includes SMTP wiring, tests, or production configuration"
    fi
  fi
  test -f "$output/scripts/test-observability.sh"
  if [[ -f "$output/web/package.json" ]]; then
    test -f "$output/package.json" ||
      fail "generated application '$name' is missing its root package-manager contract"
    test "$(jq -r '.packageManager' "$output/package.json")" = "$(jq -r '.packageManager' "$output/web/package.json")" ||
      fail "generated application '$name' has inconsistent root and web pnpm versions"
    test -f "$output/web/packages/api-client/src/generated/client.ts" ||
      fail "generated application '$name' is missing the API client entry point"
    test -f "$output/web/packages/api-client/src/generated/models/index.ts" ||
      fail "generated application '$name' is missing the generated API models"
    grep -Fq '"predev": "pnpm --filter @trykatch/api-client ensure-generated"' "$output/web/apps/web/package.json" ||
      fail "generated application '$name' does not repair its API client on the AppHost Vite path"
    bash "$output/scripts/test-proxy-headers.sh"
    (
      cd "$output"
      corepack pnpm --dir web install --frozen-lockfile
      corepack pnpm --dir web typecheck
      corepack pnpm --dir web build
    )
  fi
  if [[ $namespace_name == Horizon || $namespace_name == Trykatch ]]; then
    dotnet test "$output/tests/$namespace_name.UnitTests/$namespace_name.UnitTests.csproj" --no-build \
      --filter 'GeneratedApplicationsKeepTheirPreviewNineEventName|MovedIntegrationEventsKeepTheirExistingWireContractNames'
    dotnet test "$output/tests/$namespace_name.IntegrationTests/$namespace_name.IntegrationTests.csproj" --no-build \
      --filter PreviewNine
  fi
  # Each generated solution can produce several gigabytes of runtime assets.
  # Retain the generated source for assertions, but release build intermediates
  # before exercising the next template permutation on constrained CI runners.
  find "$output" -path '*/node_modules' -prune -o -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
}

generate_and_build Horizon
grep -Fq 'quay.io/minio/minio' "$test_root/Horizon/src/API/Horizon.AppHost/ApplicationHostingExtensions.cs" ||
  fail 'default storage-enabled template output omits the MinIO Aspire resource'
test -d "$test_root/Horizon/web"
grep -Fq 'folder that contains `Horizon.slnx`, `src/`, `tests/`, and `web/`' "$test_root/Horizon/README.md" ||
  fail 'generated React README does not identify the application root'
grep -Fq 'Use `trykatch start` when you want to run the application.' "$test_root/Horizon/README.md" ||
  fail 'generated React README does not distinguish validation from startup'
grep -Fq 'corepack pnpm --dir web install --frozen-lockfile' "$test_root/Horizon/README.md" ||
  fail 'generated React README does not install frontend dependencies before validation'
test ! -e "$test_root/Horizon/.env.local"
test ! -e "$test_root/Horizon/.idea"
test ! -e "$test_root/Horizon/.vercel"
test ! -e "$test_root/Horizon/output"
test -f "$test_root/Horizon/.github/workflows/web.yml"
test ! -e "$test_root/Horizon/compose.backend.yml"
test ! -e "$test_root/Horizon/README.backend.md"
bash "$repository_root/scripts/test-apphost-launch-profile.sh" "$test_root/Horizon"
grep -RFq --include='*.cs' 'AddViteApp("web", "../../../web/apps/web")' "$test_root/Horizon/src/API/Horizon.AppHost" ||
  fail 'React template output does not register the Vite application with AppHost'
grep -RFq --include='*.cs' 'WithDataVolume("horizon-postgres-data")' "$test_root/Horizon/src/API/Horizon.AppHost" ||
  fail 'generated PostgreSQL volume is not scoped to the application name'
grep -RFq --include='*.cs' 'WithVolume("horizon-otel-queue", "/var/lib/otelcol")' "$test_root/Horizon/src/API/Horizon.AppHost" ||
  fail 'generated OpenTelemetry queue volume is not scoped to the application name'
"$test_root/tools/trykatch" module doctor --root "$test_root/Horizon"

billing_create_output=$("$test_root/tools/trykatch" module create Billing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --fields 'number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required' \
  --root "$test_root/Horizon")
printf '%s\n' "$billing_create_output"
grep -Fq 'GET /api/v1/invoices' <<<"$billing_create_output" ||
  fail 'module creation success output omitted generated endpoints'
grep -Fq 'invoicing.read' <<<"$billing_create_output" &&
  fail 'module creation success output reported a permission for the wrong module'
grep -Fq 'billing.read' <<<"$billing_create_output" ||
  fail 'module creation success output omitted generated permissions'
grep -Fq "Run 'trykatch start'" <<<"$billing_create_output" ||
  fail 'module creation success output omitted the start command'
test -f "$test_root/Horizon/src/Modules/Billing/Horizon.Modules.Billing.Infrastructure/BillingModule.cs" ||
  fail 'backend module generation did not create its composition root'
grep -Fq 'ALTER TABLE app.invoices FORCE ROW LEVEL SECURITY' \
  "$test_root/Horizon/src/Modules/Billing/Horizon.Modules.Billing.Infrastructure/BillingModule.cs" ||
  fail 'backend module generation omitted forced PostgreSQL RLS'
grep -Fq 'public decimal Total { get; private set; }' \
  "$test_root/Horizon/src/Modules/Billing/Horizon.Modules.Billing.Domain/InvoiceRecord.cs" ||
  fail 'backend module generation did not apply its business field contract'
for event_name in InvoiceCreated InvoiceUpdated InvoiceArchived InvoiceRestored InvoiceDeletionRequested; do
  grep -Fq "record $event_name(" \
    "$test_root/Horizon/src/Modules/Billing/Horizon.Modules.Billing.IntegrationEvents/InvoiceIntegrationEvents.cs" ||
    fail "backend module generation omitted stable event contract $event_name"
done
if grep -Fq 'string Operation' \
  "$test_root/Horizon/src/Modules/Billing/Horizon.Modules.Billing.IntegrationEvents/InvoiceIntegrationEvents.cs"; then
  fail 'backend module generation retained a free-form integration event operation'
fi
for operation_id in Billing_List Billing_Get Billing_Create Billing_Update Billing_Archive Billing_Restore Billing_RequestDeletion; do
  jq -e --arg operation_id "$operation_id" \
    '[.paths[][] | select(.operationId? == $operation_id)] | length == 1' \
    "$test_root/Horizon/web/packages/api-client/openapi/Horizon.Api.json" >/dev/null ||
    fail "generated OpenAPI omitted operation id $operation_id"
done

"$test_root/tools/trykatch" module create Inventory \
  --entity Product \
  --resource inventory_items \
  --ownership organization \
  --fields 'sku:string:required:max(64),price:decimal:required,discount:decimal:optional,sequence:long:required,available:bool:required,availableAt:datetime:optional,category:enum(Standard,Premium):required,notes:string:optional:max(1000)' \
  --with-web \
  --root "$test_root/Horizon"
test -f "$test_root/Horizon/src/Modules/Inventory/Web/src/index.tsx" ||
  fail 'full-stack module generation did not create its React entrypoint'
grep -Fq 'fr:' "$test_root/Horizon/src/Modules/Inventory/Web/src/messages.ts" ||
  fail 'full-stack module generation omitted French messages'
grep -Fq "body: JSON.stringify({ sku, price, discount: discount || null, sequence, available, availableAt: availableAt === '' ? null : toUtcDateTime(availableAt, editing?.availableAt), category, notes: notes || null })" \
  "$test_root/Horizon/src/Modules/Inventory/Web/src/index.tsx" ||
  fail 'full-stack module generation did not apply its field contract to React'
inventory_web_package=$(jq -r '.entrypoints.web.specifier' \
  "$test_root/Horizon/src/Modules/Inventory/trykatch.module.json")
test "$(jq -r --arg package "$inventory_web_package" '.dependencies[$package] // empty' \
  "$test_root/Horizon/web/apps/web/package.json")" = 'workspace:*' ||
  fail 'full-stack module generation did not register its manifest-declared workspace package'

catalog_before_repeat=$(cksum "$test_root/Horizon/trykatch.modules.json")
if "$test_root/tools/trykatch" module create Billing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --root "$test_root/Horizon" >"$test_root/repeated-module-create.log" 2>&1; then
  fail 'repeated module creation unexpectedly succeeded'
fi
grep -Eq "already (registered|has source or test directories)" "$test_root/repeated-module-create.log" ||
  fail 'repeated module creation did not explain that the module already exists'
test "$(cksum "$test_root/Horizon/trykatch.modules.json")" = "$catalog_before_repeat" ||
  fail 'repeated module creation mutated the module catalog'

dotnet test "$test_root/Horizon/tests/Horizon.IntegrationTests/Horizon.IntegrationTests.csproj" \
  --no-build --no-restore \
  --filter FullyQualifiedName~EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole
generate_and_build Acme.Tools-Portal --ui none
generate_and_build Trykatch --ui none
test ! -e "$test_root/Acme.Tools.Portal/web"
grep -Fq 'folder that contains `Acme.Tools.Portal.slnx`, `src/`, and `tests/`' "$test_root/Acme.Tools.Portal/README.md" ||
  fail 'generated backend README does not identify the application root'
test ! -e "$test_root/Acme.Tools.Portal/package.json"
test ! -e "$test_root/Acme.Tools.Portal/.github/workflows/web.yml"
test ! -e "$test_root/Acme.Tools.Portal/.vercelignore"
test ! -e "$test_root/Acme.Tools.Portal/vercel.json"
test ! -e "$test_root/Acme.Tools.Portal/compose.backend.yml"
test ! -e "$test_root/Acme.Tools.Portal/README.backend.md"
test ! -e "$test_root/Acme.Tools.Portal/scripts/test-proxy-headers.sh"
test -f "$test_root/Acme.Tools.Portal/compose.yml"
test -f "$test_root/Acme.Tools.Portal/README.md"
if grep -RFq --include='*.cs' 'AddViteApp(' "$test_root/Acme.Tools.Portal/src/API/Acme.Tools.Portal.AppHost"; then
  fail 'Backend-only template output registers a Vite application'
fi
grep -Fq 'ports: ["8080:8080"]' "$test_root/Acme.Tools.Portal/compose.yml"
generate_and_build Email.Sample --ui none --email
generate_and_build Storage.Sample --ui none --storage
grep -Fq 'quay.io/minio/minio' "$test_root/Storage.Sample/src/API/Storage.Sample.AppHost/ApplicationHostingExtensions.cs" ||
  fail 'explicit storage-enabled template output omits the MinIO Aspire resource'
dotnet new trykatch -n NoStorage -o "$test_root/NoStorage" --storage false --allow-scripts yes --debug:custom-hive "$template_hive"
if grep -Fq 'quay.io/minio/minio' "$test_root/NoStorage/src/API/NoStorage.AppHost/ApplicationHostingExtensions.cs"; then
  fail 'storage-disabled template output unexpectedly registers MinIO'
fi
generate_and_build Documents.Sample --ui none --documents
generate_and_build Images.Sample --ui none --images
generate_and_build Everything.Sample --ui none --email --storage --documents --images
