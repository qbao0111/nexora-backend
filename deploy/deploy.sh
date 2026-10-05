#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ "${1:-}" =~ ^[0-9a-f]{40}$ ]] || { echo "A full commit SHA is required." >&2; exit 1; }
[[ "$(id -u)" != 0 ]] || { echo "Deploy as the non-root deploy user." >&2; exit 1; }
cd /opt/nexora
# Serialize manual and automated releases, including rollback.
exec 9>.deploy.lock
flock -n 9 || { echo "Another release is running." >&2; exit 1; }
[[ -f .env.production && "$(stat -c %a .env.production)" == 600 ]] || {
    echo "Provision .env.production with mode 600 before release." >&2; exit 1;
}
export NEXORA_IMAGE_TAG="$1"
compose=(docker compose -f docker-compose.production.yml)
"${compose[@]}" config --quiet
"${compose[@]}" pull
old_id="$("${compose[@]}" ps -aq nexora)"
if [[ -n "$old_id" ]]; then
    docker inspect --format '{{.Config.Image}}' "$old_id" > previous-image.txt
fi
# No overlapping API/Worker/migrator on this VPS. This is a short downtime release.
"${compose[@]}" stop
"${compose[@]}" up -d --remove-orphans
container_id="$("${compose[@]}" ps -aq nexora)"
for (( attempt=0; attempt<120; attempt++ )); do
    health="$(docker inspect --format '{{.State.Health.Status}}' "$container_id")"
    if [[ "$health" == healthy ]]; then
        # Liveness alone does not prove database readiness.
        docker exec "$container_id" curl --fail --silent --output /dev/null --max-time 10 http://localhost:10000/api/v1/health
        printf '%s\n' "$NEXORA_IMAGE_TAG" > current-image-sha.txt
        echo "Release healthy: $NEXORA_IMAGE_TAG"
        exit 0
    fi
    if [[ "$health" == unhealthy ]]; then break; fi
    sleep 3
done
echo "Release failed health verification. Inspect private logs; no automatic DB rollback." >&2
exit 1
