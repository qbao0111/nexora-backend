#!/usr/bin/env bash
set -euo pipefail
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
test_dir="$(mktemp -d)"
runner=""
cleanup() {
    if [[ -n "$runner" ]]; then kill -TERM "$runner" 2>/dev/null || true; wait "$runner" 2>/dev/null || true; fi
    rm -rf -- "$test_dir"
}
trap cleanup EXIT
cp "$repo/scripts/container-entrypoint.sh" "$test_dir/entrypoint.sh"
mkdir -p "$test_dir/bin"
cat > "$test_dir/nexora-migrate" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ $# == 0 ]] || exit 90
echo migrate >> "$EVENTS"
if [[ "${MIGRATE_WAIT:-false}" == true ]]; then
    trap 'echo migration-stopped >> "$EVENTS"; exit 0' TERM
    while true; do sleep 0.1; done
fi
exit "${MIGRATE_STATUS:-0}"
SH
cat > "$test_dir/bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
name=worker
[[ "$1" != *Nexora.Api.dll ]] || name=api
echo "$name-start" >> "$EVENTS"
trap 'echo "$name-stopped" >> "$EVENTS"; exit 0' TERM
if [[ "$name" == "${EXIT_CHILD:-none}" ]]; then sleep 0.3; exit "${CHILD_STATUS:-0}"; fi
while true; do sleep 0.1; done
SH
chmod +x "$test_dir/nexora-migrate" "$test_dir/bin/dotnet"
export PATH="$test_dir/bin:$PATH" EVENTS="$test_dir/events"
export ASPNETCORE_ENVIRONMENT=Staging DOTNET_ENVIRONMENT=Staging
export ConnectionStrings__Postgres=TEST_ONLY_NOT_A_REAL_CONNECTION Storage__Local__RootPath="$test_dir/storage"
export TEST_ENTRYPOINT="$test_dir/entrypoint.sh"
await_event() {
    for (( i=0; i<100; i++ )); do
        if grep -q "$1" "$EVENTS" 2>/dev/null; then return; fi
        sleep 0.05
    done
    echo "Missing event: $1" >&2; exit 1
}
run_exit_case() {
    : > "$EVENTS"
    local status=0
    timeout 10 bash "$test_dir/entrypoint.sh" > "$test_dir/log" 2>&1 || status=$?
    [[ "$status" == "$1" ]] || { cat "$test_dir/log"; echo "Wrong exit: $status"; exit 1; }
    ! grep -q TEST_ONLY_NOT_A_REAL_CONNECTION "$test_dir/log"
}
MIGRATE_STATUS=17 run_exit_case 17
! grep -q -- '-start' "$EVENTS"
ConnectionStrings__Postgres= run_exit_case 1
[[ ! -s "$EVENTS" ]]
DOTNET_ENVIRONMENT=Production run_exit_case 1
[[ ! -s "$EVENTS" ]]
for EXIT_CHILD in api worker; do
    export EXIT_CHILD CHILD_STATUS=7
    run_exit_case 7
    [[ "$(head -n 1 "$EVENTS")" == migrate ]]
    grep -q -- '-stopped' "$EVENTS"
    CHILD_STATUS=0 run_exit_case 1
done
unset EXIT_CHILD CHILD_STATUS
for signal in TERM INT; do
    if [[ "$signal" == INT && "${OSTYPE:-}" == msys* ]]; then
        echo "SIGINT launcher check requires Linux; covered by hosted container CI."
        continue
    fi
    : > "$EVENTS"
    # Noninteractive asynchronous Bash ignores SIGINT unless reset by the launcher.
    if [[ "$signal" == INT ]]; then
        python3 -c 'import os,signal; signal.signal(signal.SIGINT, signal.SIG_DFL); os.execvp("bash",["bash",os.environ["TEST_ENTRYPOINT"]])' > "$test_dir/log" 2>&1 &
    else
        bash "$test_dir/entrypoint.sh" > "$test_dir/log" 2>&1 &
    fi
    runner=$!
    await_event api-start
    await_event worker-start
    kill -"$signal" "$runner"
    wait "$runner"
    runner=""
    grep -q api-stopped "$EVENTS"
    grep -q worker-stopped "$EVENTS"
done
: > "$EVENTS"
MIGRATE_WAIT=true bash "$test_dir/entrypoint.sh" > "$test_dir/log" 2>&1 &
runner=$!
await_event migrate
kill -TERM "$runner"
wait "$runner"
runner=""
grep -q migration-stopped "$EVENTS"
! grep -q -- '-start' "$EVENTS"
echo "Entrypoint regression checks passed."
