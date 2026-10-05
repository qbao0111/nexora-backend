#!/usr/bin/env bash
set -euo pipefail

app_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$app_dir"
: "${ASPNETCORE_ENVIRONMENT:?Hosting must set ASPNETCORE_ENVIRONMENT}"
: "${DOTNET_ENVIRONMENT:?Hosting must set DOTNET_ENVIRONMENT}"
: "${ConnectionStrings__Postgres:?Hosting must set ConnectionStrings__Postgres}"
if [[ "$ASPNETCORE_ENVIRONMENT" != "$DOTNET_ENVIRONMENT" ]]; then
    echo "API and Worker environments must agree." >&2
    exit 1
fi
if [[ ! -x ./nexora-migrate ]]; then
    echo "Migration bundle is missing or not executable." >&2
    exit 1
fi

export Storage__Local__RootPath="${Storage__Local__RootPath:-/tmp/nexora-storage}"
mkdir -p "$Storage__Local__RootPath"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${PORT:-10000}}"
children=()
stop_children() {
    trap '' TERM INT
    if (( ${#children[@]} > 0 )); then
        kill -TERM "${children[@]}" 2>/dev/null || true
        for child in "${children[@]}"; do wait "$child" 2>/dev/null || true; done
    fi
}
trap 'stop_children; exit 0' TERM INT

echo "Applying migrations before application startup."
# The design-time factory reads ConnectionStrings__Postgres. Never put it in argv.
./nexora-migrate &
children+=("$!")
migration_status=0
wait "${children[0]}" || migration_status=$?
children=()
if (( migration_status != 0 )); then
    echo "Migration failed; API and Worker will not start." >&2
    exit "$migration_status"
fi

echo "Starting API and Worker."
dotnet ./worker/Nexora.Worker.dll &
children+=("$!")
dotnet ./api/Nexora.Api.dll &
children+=("$!")
status=0
wait -n "${children[@]}" || status=$?
echo "Application child exited unexpectedly; stopping sibling." >&2
stop_children
# A clean child exit is also unexpected while the container is serving traffic.
if (( status == 0 )); then status=1; fi
exit "$status"
