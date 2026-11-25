#!/usr/bin/env bash
# Validate the public API health contract before browser or deployment smoke tests.
#
# A liveness-only probe can pass while the API cannot reach required dependencies.
# This gate waits for both liveness and readiness, then validates the safe response
# shape, cache policy, and request correlation contract.
#
# Validation is bound to the terminal HTTP response only. A redirect hop that carries
# Cache-Control, Pragma, or X-Request-ID does not vouch for the health document that
# was actually served, so those headers must be present on the final response block.
set -euo pipefail

BASE_URL="${1:-${API_BASE_URL:-http://127.0.0.1:5000}}"
BASE_URL="${BASE_URL%/}"
MAX_ATTEMPTS="${SMOKE_MAX_ATTEMPTS:-60}"
RETRY_SECONDS="${SMOKE_RETRY_SECONDS:-2}"
CONNECT_TIMEOUT_SECONDS="${SMOKE_CONNECT_TIMEOUT_SECONDS:-3}"
REQUEST_TIMEOUT_SECONDS="${SMOKE_REQUEST_TIMEOUT_SECONDS:-10}"

if [[ -z "$BASE_URL" || "$BASE_URL" == *$'\n'* || "$BASE_URL" == *$'\r'* ]]; then
  echo "[api-health-smoke] a non-empty API base URL is required" >&2
  exit 2
fi

if ! [[ "$MAX_ATTEMPTS" =~ ^[1-9][0-9]*$ && "$RETRY_SECONDS" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "[api-health-smoke] SMOKE_MAX_ATTEMPTS must be positive and SMOKE_RETRY_SECONDS must be non-negative" >&2
  exit 2
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

validate_response() {
  local headers_file="$1"
  local body_file="$2"
  local endpoint="$3"
  python3 - "$headers_file" "$body_file" "$endpoint" <<'PY'
import json
import sys
from pathlib import Path

headers_path, body_path, endpoint = sys.argv[1:]
# curl --dump-header appends every response block of a redirect chain to the same
# file. The cache policy, correlation id, and status must therefore be read from
# the terminal response that actually produced the body; a header carried only by
# an earlier 3xx hop must never satisfy the contract for the health document.
blocks: list[list[str]] = []
for line in Path(headers_path).read_text(errors="replace").splitlines():
    if line.lower().startswith("http/"):
        blocks.append([])
    if blocks:
        blocks[-1].append(line)

if not blocks:
    raise SystemExit(
        f"{endpoint}: no HTTP response block was captured; refusing to infer a safe response"
    )

headers = {}
terminal_status = None
for position, line in enumerate(blocks[-1]):
    if position == 0:
        fields = line.split()
        if len(fields) < 2 or not fields[1].isdigit():
            raise SystemExit(f"{endpoint}: terminal HTTP status line is not parseable: {line!r}")
        terminal_status = int(fields[1])
        continue
    if ":" not in line:
        continue
    name, value = line.split(":", 1)
    headers[name.lower()] = value.strip()

if terminal_status != 200:
    raise SystemExit(f"{endpoint}: terminal HTTP response status is {terminal_status}, not 200")

cache_control = headers.get("cache-control", "").lower()
if "no-store" not in cache_control:
    raise SystemExit(f"{endpoint}: terminal response is missing Cache-Control: no-store")
if "no-cache" not in headers.get("pragma", "").lower():
    raise SystemExit(f"{endpoint}: terminal response is missing Pragma: no-cache")
request_id = headers.get("x-request-id", "")
if not request_id:
    raise SystemExit(f"{endpoint}: terminal response is missing X-Request-ID")

try:
    payload = json.loads(Path(body_path).read_text())
except json.JSONDecodeError as exc:
    raise SystemExit(f"{endpoint}: response is not JSON: {exc}") from exc

if payload.get("service") != "Taslim API":
    raise SystemExit(f"{endpoint}: unexpected service value")
if not isinstance(payload.get("requestId"), str) or payload["requestId"] != request_id:
    raise SystemExit(f"{endpoint}: response requestId does not match X-Request-ID")
checks = payload.get("checks")
if not isinstance(checks, list) or not checks:
    raise SystemExit(f"{endpoint}: checks must be a non-empty array")

if endpoint == "/health/live":
    if payload.get("status") != "alive":
        raise SystemExit(f"{endpoint}: status is not alive")
    process = next((check for check in checks if check.get("name") == "process"), None)
    if process is None or process.get("status") != "alive" or process.get("required") is not True:
        raise SystemExit(f"{endpoint}: process check is not alive and required")
else:
    if payload.get("status") != "ready":
        raise SystemExit(f"{endpoint}: status is not ready")
    database = next((check for check in checks if check.get("name") == "database"), None)
    if database is None or database.get("status") != "available" or database.get("required") is not True:
        raise SystemExit(f"{endpoint}: required database check is not available")
    for check in checks:
        if check.get("required") is True and check.get("status") != "available":
            raise SystemExit(f"{endpoint}: required check {check.get('name', '<unnamed>')} is not available")

response_text = Path(body_path).read_text(errors="replace")
for forbidden in ("password", "secretKey", "accessKey", "Host="):
    if forbidden.lower() in response_text.lower():
        raise SystemExit(f"{endpoint}: response contains forbidden diagnostic material: {forbidden}")
PY
}

probe_endpoint() {
  local endpoint="$1"
  local label="${endpoint#/}"
  local headers_file="$TMP_DIR/${label//\//_}.headers"
  local body_file="$TMP_DIR/${label//\//_}.body"
  local attempt status

  for ((attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)); do
    : > "$headers_file"
    : > "$body_file"
    status="$(curl --silent --show-error --location \
      --connect-timeout "$CONNECT_TIMEOUT_SECONDS" \
      --max-time "$REQUEST_TIMEOUT_SECONDS" \
      --dump-header "$headers_file" \
      --output "$body_file" \
      --write-out '%{http_code}' \
      "$BASE_URL$endpoint" 2>"$TMP_DIR/curl.error" || true)"

    if [[ "$status" == "200" ]]; then
      if validate_response "$headers_file" "$body_file" "$endpoint"; then
        printf '%s\n' "[api-health-smoke] $endpoint passed on attempt $attempt"
        return 0
      fi
      echo "[api-health-smoke] $endpoint returned an invalid response on attempt $attempt" >&2
      cat "$TMP_DIR/curl.error" >&2 || true
    elif [[ "$attempt" == "$MAX_ATTEMPTS" ]]; then
      echo "[api-health-smoke] $endpoint did not return HTTP 200 (last status: ${status:-curl-error})" >&2
      cat "$TMP_DIR/curl.error" >&2 || true
      return 1
    fi

    if (( attempt < MAX_ATTEMPTS )); then
      sleep "$RETRY_SECONDS"
    fi
  done
  return 1
}

printf '%s\n' "[api-health-smoke] checking $BASE_URL"
probe_endpoint "/health/live"
probe_endpoint "/health/ready"
printf '%s\n' "[api-health-smoke] PASS — liveness and required dependency readiness are healthy"
