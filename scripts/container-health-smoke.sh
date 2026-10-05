#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${1:?usage: $0 IMAGE [PORT] [TIMEOUT_SECONDS]}"
port="${2:-8080}"
timeout_seconds="${3:-120}"
container_name="${CONTAINER_NAME:-taslim-api-release-smoke}"
connection_string="${SMOKE_CONNECTION_STRING:-Host=host.docker.internal;Port=5432;Database=taslim_container_smoke;Username=taslim;Password=change-me}"

if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
    echo "Container smoke port must be a valid TCP port." >&2
    exit 2
fi
if ! [[ "$timeout_seconds" =~ ^[0-9]+$ ]] || (( timeout_seconds < 1 )); then
    echo "Container smoke timeout must be a positive number of seconds." >&2
    exit 2
fi

started=0
passed=0
cleanup() {
    if (( started == 1 && passed == 0 )); then
        echo "API container logs (smoke failed):" >&2
        docker logs "$container_name" >&2 || true
    fi
    if (( started == 1 )); then
        docker rm --force "$container_name" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

docker rm --force "$container_name" >/dev/null 2>&1 || true
docker run --detach \
    --add-host host.docker.internal:host-gateway \
    --publish "${port}:${port}" \
    --name "$container_name" \
    --env ASPNETCORE_ENVIRONMENT=Testing \
    --env ASPNETCORE_URLS="http://0.0.0.0:${port}" \
    --env ASPNETCORE_HTTP_PORTS="$port" \
    --env "ConnectionStrings__Postgres=${connection_string}" \
    --env Database__ApplyMigrations=true \
    --env GenerationJobs__WorkerEnabled=false \
    --env Autopilot__WatchdogEnabled=false \
    --env Files__StorageProvider=Local \
    --env Files__LocalRootPath=/tmp/taslim-release-smoke-files \
    "$image" >/dev/null
started=1

smoke_attempts="$timeout_seconds"
SMOKE_MAX_ATTEMPTS="$smoke_attempts" \
SMOKE_RETRY_SECONDS=2 \
SMOKE_CONNECT_TIMEOUT_SECONDS=2 \
SMOKE_REQUEST_TIMEOUT_SECONDS=5 \
bash "$repo_root/scripts/api-health-smoke.sh" "http://127.0.0.1:${port}"

echo "API container smoke passed: migrations, liveness, and required dependency readiness are healthy."
passed=1
