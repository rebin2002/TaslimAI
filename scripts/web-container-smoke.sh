#!/usr/bin/env bash
set -euo pipefail

image="${1:?usage: $0 IMAGE [PORT] [TIMEOUT_SECONDS]}"
port="${2:-3000}"
timeout_seconds="${3:-120}"
container_name="${CONTAINER_NAME:-taslim-web-release-smoke}"

if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
    echo "Web container smoke port must be a valid TCP port." >&2
    exit 2
fi
if ! [[ "$timeout_seconds" =~ ^[0-9]+$ ]] || (( timeout_seconds < 1 )); then
    echo "Web container smoke timeout must be a positive number of seconds." >&2
    exit 2
fi

started=0
passed=0
tmp_dir="$(mktemp -d)"
cleanup() {
    if (( started == 1 && passed == 0 )); then
        echo "Web container logs (smoke failed):" >&2
        docker logs "$container_name" >&2 || true
    fi
    if (( started == 1 )); then
        docker rm --force "$container_name" >/dev/null 2>&1 || true
    fi
    rm -rf "$tmp_dir"
}
trap cleanup EXIT

docker rm --force "$container_name" >/dev/null 2>&1 || true
docker run --detach --network host --name "$container_name" \
    --env NODE_ENV=production \
    --env PORT="$port" \
    --env HOSTNAME=0.0.0.0 \
    "$image" >/dev/null
started=1

validate_response() {
    local headers_file="$1"
    local body_file="$2"
    python3 - "$headers_file" "$body_file" <<'PY'
import sys
from pathlib import Path

headers_path, body_path = map(Path, sys.argv[1:])
headers = {}
for line in headers_path.read_text(errors="replace").splitlines():
    if ":" not in line:
        continue
    name, value = line.split(":", 1)
    headers[name.lower()] = value.strip().lower()

content_type = headers.get("content-type", "")
if "text/html" not in content_type:
    raise SystemExit("web root did not return an HTML content type")
if headers.get("x-content-type-options") != "nosniff":
    raise SystemExit("web root is missing X-Content-Type-Options: nosniff")
if headers.get("x-frame-options") != "deny":
    raise SystemExit("web root is missing X-Frame-Options: DENY")
body = body_path.read_text(errors="replace")
if not body.strip() or "<html" not in body.lower():
    raise SystemExit("web root did not return a non-empty HTML document")
PY
}

headers_file="$tmp_dir/root.headers"
body_file="$tmp_dir/root.body"
deadline=$((SECONDS + timeout_seconds))
while (( SECONDS < deadline )); do
    : > "$headers_file"
    : > "$body_file"
    status="$(curl --silent --show-error --location \
        --connect-timeout 3 \
        --max-time 5 \
        --dump-header "$headers_file" \
        --output "$body_file" \
        --write-out '%{http_code}' \
        "http://127.0.0.1:${port}/" 2>/dev/null || true)"
    if [[ "$status" == "200" ]] && validate_response "$headers_file" "$body_file"; then
        echo "Web container smoke passed: root document and security headers are healthy."
        passed=1
        exit 0
    fi
    sleep 2
done

echo "Timed out waiting for the web container root document on port ${port}." >&2
exit 1
