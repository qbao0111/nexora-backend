#!/bin/bash
set -euo pipefail

# Default to Staging environment and keep the API/Worker host modes identical.
CONFIGURED_ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-}"
CONFIGURED_DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-}"
if [ -n "$CONFIGURED_ASPNETCORE_ENVIRONMENT" ] &&
   [ -n "$CONFIGURED_DOTNET_ENVIRONMENT" ] &&
   [ "${CONFIGURED_ASPNETCORE_ENVIRONMENT,,}" != "${CONFIGURED_DOTNET_ENVIRONMENT,,}" ]; then
    echo "ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT must match." >&2
    exit 1
fi
DEPLOYMENT_ENVIRONMENT="${CONFIGURED_ASPNETCORE_ENVIRONMENT:-${CONFIGURED_DOTNET_ENVIRONMENT:-Staging}}"
export ASPNETCORE_ENVIRONMENT="$DEPLOYMENT_ENVIRONMENT"
export DOTNET_ENVIRONMENT="$DEPLOYMENT_ENVIRONMENT"

# Render exposes the source commit to the container. Preserve an explicit
# release, otherwise use that same safe identifier for API and Worker.
if [ -z "${Sentry__Release:-}" ] && [ -n "${RENDER_GIT_COMMIT:-}" ]; then
    export Sentry__Release="$RENDER_GIT_COMMIT"
fi

# Only the local development adapter needs a filesystem directory. R2 is the
# default for this staging image; startup validation still rejects unknown or
# incomplete provider configuration.
STORAGE_PROVIDER="${Storage__Provider:-r2}"
case "${STORAGE_PROVIDER,,}" in
    local)
        STORAGE_PATH="${Storage__Local__RootPath:-/tmp/nexora-storage}"
        mkdir -p "$STORAGE_PATH"
        ;;
    r2)
        ;;
    *)
        echo "Storage:Provider must be local or r2." >&2
        exit 1
        ;;
esac

# Run EF Core migration bundle exactly once before starting services
if [ -f "/app/nexora-migrate" ] && [ -n "${ConnectionStrings__Postgres:-}" ]; then
    echo "Running EF Core migration bundle..."
    /app/nexora-migrate --connection "$ConnectionStrings__Postgres"
    echo "EF Core migration bundle completed successfully."
fi

PORT="${PORT:-10000}"
export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"

echo "Starting Nexora.Worker in background..."
dotnet /app/worker/Nexora.Worker.dll &
WORKER_PID=$!

echo "Starting Nexora.Api on ${ASPNETCORE_URLS} in background..."
dotnet /app/api/Nexora.Api.dll &
API_PID=$!

cleanup() {
    echo "Termination signal received. Gracefully stopping Nexora.Api (PID $API_PID) and Nexora.Worker (PID $WORKER_PID)..."
    kill -TERM "$API_PID" 2>/dev/null || true
    kill -TERM "$WORKER_PID" 2>/dev/null || true
    wait "$API_PID" 2>/dev/null || true
    wait "$WORKER_PID" 2>/dev/null || true
    echo "All processes stopped."
    exit 0
}

trap cleanup SIGTERM SIGINT

# Monitor child processes: if either exits unexpectedly, shut down the sibling and exit with its code.
while true; do
    if ! kill -0 "$API_PID" 2>/dev/null; then
        wait "$API_PID" || API_STATUS=$?
        echo "Nexora.Api exited unexpectedly with code ${API_STATUS:-0}."
        kill -TERM "$WORKER_PID" 2>/dev/null || true
        wait "$WORKER_PID" 2>/dev/null || true
        exit "${API_STATUS:-1}"
    fi

    if ! kill -0 "$WORKER_PID" 2>/dev/null; then
        wait "$WORKER_PID" || WORKER_STATUS=$?
        echo "Nexora.Worker exited unexpectedly with code ${WORKER_STATUS:-0}."
        kill -TERM "$API_PID" 2>/dev/null || true
        wait "$API_PID" 2>/dev/null || true
        exit "${WORKER_STATUS:-1}"
    fi

    sleep 1
done
