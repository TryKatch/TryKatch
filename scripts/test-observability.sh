#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ${1:-} == --static ]]; then
  exec bash "$repository_root/templates/trykatch/scripts/test-observability.sh" --static
fi

exec bash "$repository_root/templates/trykatch/scripts/test-observability.sh" "$repository_root/templates/trykatch"
