#!/usr/bin/env bash
set -euo pipefail

image="${1:?usage: $0 IMAGE [PORT] [TIMEOUT_SECONDS]}"
port="${2:-8080}"
timeout_seconds="${3:-120}"
container_name="${CONTAINER_NAME:-taslim-api-release-smoke}"
connection_string="${SMOKE_CONNECTION_STRING:-Host=127.0.0.1;Port=5432;Database=taslim_container_smoke;Username=taslim;Password=change-me}"

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
docker run --detach --network host --name "$container_name" \
    --env ASPNETCORE_ENVIRONMENT=Testing \
    --env ASPNETCORE_URLS="http://0.0.0.0:${port}" \
    --env ASPNETCORE_HTTP_PORTS="$port" \
    --env "ConnectionStrings__Postgres=${connection_string}" \
    --env Database__ApplyMigrations=false \
    --env GenerationJobs__WorkerEnabled=false \
    --env Autopilot__WatchdogEnabled=false \
    --env Files__StorageProvider=Local \
    --env Files__LocalRootPath=/tmp/taslim-release-smoke-files \
    "$image" >/dev/null
started=1

wait_for_status() {
    local path="$1"
    local expected_status="$2"
    local deadline=$((SECONDS + timeout_seconds))
    local body

    while (( SECONDS < deadline )); do
        if body="$(curl --silent --show-error --fail --max-time 5 "http://127.0.0.1:${port}${path}" 2>/dev/null)" \
            && jq -e --arg expected "$expected_status" '.status == $expected' >/dev/null 2>&1 <<<"$body"; then
            echo "${path} returned ${expected_status}."
            return 0
        fi
        sleep 2
    done

    echo "Timed out waiting for ${path} to return ${expected_status}." >&2
    return 1
}

wait_for_status "/health/live" "alive"
wait_for_status "/health/ready" "ready"
echo "API container smoke passed: liveness and readiness are healthy."
passed=1
