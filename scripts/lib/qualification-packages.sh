#!/usr/bin/env bash
# Sourced by isolated qualification harnesses. Prepared release candidates are
# validated and consumed without repacking or resolving tools from public feeds.
prepare_qualification_packages() {
  local output=$1
  local default_cli_version=${2:-0.1.0-ci}
  if [[ -n ${TRYKATCH_QUALIFICATION_PACKAGES:-} ]]; then
    : "${TRYKATCH_QUALIFICATION_VERSION:?Prepared packages require their release version}"
    : "${TRYKATCH_QUALIFICATION_MANIFEST:?Prepared packages require their manifest}"
    : "${TRYKATCH_QUALIFICATION_RUN_ID:?Prepared packages require their qualification run ID}"
    qualification_package_feed=$(cd "$TRYKATCH_QUALIFICATION_PACKAGES" && pwd -P)
    qualification_cli_version=$TRYKATCH_QUALIFICATION_VERSION
    node "$repository_root/scripts/release-artifacts.mjs" verify \
      "$qualification_package_feed" "$TRYKATCH_QUALIFICATION_MANIFEST" \
      "$qualification_cli_version" "$(git -C "$repository_root" rev-parse HEAD)" "$TRYKATCH_QUALIFICATION_RUN_ID"
    qualification_template_package="$qualification_package_feed/Trykatch.Templates.$qualification_cli_version.nupkg"
  else
    qualification_package_feed=$output
    qualification_cli_version=$default_cli_version
    dotnet pack "$repository_root/Trykatch.Templates.csproj" -c Release -o "$output"
    qualification_template_package=$(find "$output" -name 'Trykatch.Templates.*.nupkg' -print -quit)
    dotnet pack "$repository_root/templates/trykatch/tools/Trykatch.ModuleTool" \
      -c Release -o "$output" -p:PackageVersion="$qualification_cli_version"
  fi
}

install_qualification_cli() {
  local workspace=$1
  NUGET_PACKAGES="$workspace/tool-package-cache" dotnet tool install Trykatch.Cli \
    --version "$qualification_cli_version" --tool-path "$workspace/tools" \
    --configfile "$repository_root/scripts/lib/qualification.nuget.config" \
    --add-source "$qualification_package_feed"
}
