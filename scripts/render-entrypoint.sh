#!/bin/bash
set -euo pipefail

# Ensure storage directory exists
STORAGE_PATH="${Storage__Local__RootPath:-/tmp/nexora-storage}"
mkdir -p "$STORAGE_PATH"

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
