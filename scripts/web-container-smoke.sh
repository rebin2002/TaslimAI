#!/usr/bin/env bash
# Validate the production web container's terminal HTTP responses and private SSR cache fence.
#
# Authentication is hermetic and synthetic: a test-only Node preload responds to exactly
# /api/auth/me for one fixed smoke cookie and rejects every other fetch. No API, provider,
# credential, or production network call is made by these probes.
set -euo pipefail

image="${1:?usage: $0 IMAGE [PORT] [TIMEOUT_SECONDS]}"
port="${2:-3000}"
timeout_seconds="${3:-120}"
container_name="${CONTAINER_NAME:-taslim-web-release-smoke}"
smoke_cookie='taslim.auth=web-container-smoke-synthetic-session'

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
auth_stub_dir="$(mktemp -d)"
chmod 755 "$auth_stub_dir"
cat > "$auth_stub_dir/auth-smoke.cjs" <<'JS'
// Test-only session authority for the built production container. This stub is
// mounted read-only and installed only in this disposable smoke container.
// It never opens a socket: every fetch except the exact auth probe fails closed.
const syntheticCookie = "taslim.auth=web-container-smoke-synthetic-session";

globalThis.fetch = async function smokeFetch(input, init = {}) {
  const requestUrl = new URL(typeof input === "string" || input instanceof URL ? input : input.url);
  if (requestUrl.pathname !== "/api/auth/me") {
    throw new Error(`web smoke blocked unexpected outbound fetch to ${requestUrl.pathname}`);
  }

  const suppliedHeaders = init.headers ?? (input instanceof Request ? input.headers : undefined);
  const headers = new Headers(suppliedHeaders);
  if (headers.get("cookie") !== syntheticCookie) {
    return Response.json({ error: "unauthorized" }, { status: 401 });
  }

  return Response.json({
    user: { id: "web-smoke-user", personalWorkspaceId: "web-smoke-workspace" },
    personalWorkspace: { id: "web-smoke-workspace", type: "Personal", role: "Owner" },
  }, { headers: { "Cache-Control": "no-store" } });
};
JS
chmod 644 "$auth_stub_dir/auth-smoke.cjs"
cleanup() {
    if (( started == 1 && passed == 0 )); then
        echo "Web container logs (smoke failed):" >&2
        docker logs "$container_name" >&2 || true
    fi
    if (( started == 1 )); then
        docker rm --force "$container_name" >/dev/null 2>&1 || true
    fi
    rm -rf "$tmp_dir" "$auth_stub_dir"
}
trap cleanup EXIT

docker rm --force "$container_name" >/dev/null 2>&1 || true
docker run --detach --publish "${port}:${port}" --name "$container_name" \
    --mount "type=bind,src=$auth_stub_dir,dst=/tmp/web-container-smoke-auth,readonly" \
    --env NODE_OPTIONS=--require=/tmp/web-container-smoke-auth/auth-smoke.cjs \
    --env NODE_ENV=production \
    --env PORT="$port" \
    --env HOSTNAME=0.0.0.0 \
    "$image" >/dev/null
started=1

validate_response() {
    local headers_file="$1"
    local body_file="$2"
    local observed_status="$3"
    local expected_status="$4"
    local label="$5"
    local expected_location="${6:-}"
    local expect_html="${7:-0}"
    python3 - "$headers_file" "$body_file" "$observed_status" "$expected_status" "$label" "$expected_location" "$expect_html" <<'PY'
import sys
from pathlib import Path

headers_path, body_path = map(Path, sys.argv[1:3])
observed_status, expected_status, label, expected_location, expect_html = sys.argv[3:]
# curl may record a redirect chain; the terminal response alone must satisfy the
# contract for the document/payload actually returned by the production image.
blocks: list[list[str]] = []
for line in headers_path.read_text(errors="replace").splitlines():
    if line.lower().startswith("http/"):
        blocks.append([])
    if blocks:
        blocks[-1].append(line)

if not blocks:
    raise SystemExit(f"{label}: no HTTP response block was captured; refusing to infer a safe response")

headers: dict[str, str] = {}
terminal_status = None
for position, line in enumerate(blocks[-1]):
    if position == 0:
        fields = line.split()
        if len(fields) < 2 or not fields[1].isdigit():
            raise SystemExit(f"{label}: terminal HTTP status line is not parseable: {line!r}")
        terminal_status = int(fields[1])
        continue
    if ":" not in line:
        continue
    name, value = line.split(":", 1)
    headers[name.lower()] = value.strip().lower()

if terminal_status != int(observed_status):
    raise SystemExit(f"{label}: curl observed status {observed_status}, but terminal header block says {terminal_status}")
if terminal_status != int(expected_status):
    raise SystemExit(f"{label}: terminal HTTP status is {terminal_status}, expected {expected_status}")

if expect_html == "1":
    if "text/html" not in headers.get("content-type", ""):
        raise SystemExit(f"{label}: terminal response did not return an HTML content type")
    if headers.get("x-content-type-options") != "nosniff":
        raise SystemExit(f"{label}: terminal response is missing X-Content-Type-Options: nosniff")
    if headers.get("x-frame-options") != "deny":
        raise SystemExit(f"{label}: terminal response is missing X-Frame-Options: DENY")
    body = body_path.read_text(errors="replace")
    if not body.strip() or "<html" not in body.lower():
        raise SystemExit(f"{label}: terminal response did not return a non-empty HTML document")

cache_control = headers.get("cache-control", "")
directives = [directive.strip() for directive in cache_control.split(",") if directive.strip()]
required_directives = {"private", "no-cache", "no-store", "must-revalidate", "max-age=0"}
missing_directives = sorted(required_directives.difference(directives))
if missing_directives:
    raise SystemExit(f"{label}: Cache-Control is missing required directive(s): {', '.join(missing_directives)}")
for directive in directives:
    name, separator, value = directive.partition("=")
    name = name.strip()
    value = value.strip()
    if name in {"public", "s-maxage"}:
        raise SystemExit(f"{label}: shared-cacheable Cache-Control directive is present: {directive}")
    if name == "max-age" and (not separator or value != "0"):
        raise SystemExit(f"{label}: Cache-Control max-age is not zero: {directive}")

robots = {directive.strip() for directive in headers.get("x-robots-tag", "").split(",") if directive.strip()}
required_robots = {"noindex", "nofollow", "noarchive", "nosnippet"}
missing_robots = sorted(required_robots.difference(robots))
if missing_robots:
    raise SystemExit(f"{label}: X-Robots-Tag is missing directive(s): {', '.join(missing_robots)}")

if expected_location and headers.get("location") != expected_location.lower():
    raise SystemExit(f"{label}: Location is {headers.get('location')!r}, expected {expected_location!r}")
PY
}

capture_response() {
    local name="$1"
    local request_path="$2"
    local cookie="${3:-}"
    local follow_redirects="${4:-0}"
    local preserve_raw_path="${5:-0}"
    local headers_file="$tmp_dir/$name.headers"
    local body_file="$tmp_dir/$name.body"
    local -a curl_args=(
        --silent --show-error
        --connect-timeout 3
        --max-time 15
        --dump-header "$headers_file"
        --output "$body_file"
        --write-out '%{http_code}'
    )
    if [[ "$follow_redirects" == "1" ]]; then
        curl_args+=(--location --max-redirs 5)
    fi
    if [[ "$preserve_raw_path" == "1" ]]; then
        # Essential: without --path-as-is curl normalizes dot segments before the
        # request leaves the runner, making this a client-URL test rather than an
        # ingress/matcher boundary proof.
        curl_args+=(--path-as-is)
    fi
    if [[ -n "$cookie" ]]; then
        curl_args+=(--header "Cookie: $cookie")
    fi
    CAPTURED_STATUS="$(curl "${curl_args[@]}" "http://127.0.0.1:${port}${request_path}" 2>/dev/null || true)"
    CAPTURED_HEADERS="$headers_file"
    CAPTURED_BODY="$body_file"
}

check_response() {
    local name="$1"
    local request_path="$2"
    local expected_status="$3"
    local cookie="${4:-}"
    local follow_redirects="${5:-0}"
    local preserve_raw_path="${6:-0}"
    local expected_location="${7:-}"
    local expect_html="${8:-0}"
    capture_response "$name" "$request_path" "$cookie" "$follow_redirects" "$preserve_raw_path"
    validate_response "$CAPTURED_HEADERS" "$CAPTURED_BODY" "$CAPTURED_STATUS" "$expected_status" "$name ($request_path)" "$expected_location" "$expect_html"
}

deadline=$((SECONDS + timeout_seconds))
while (( SECONDS < deadline )); do
    capture_response root "/" "" 1 0
    if [[ "$CAPTURED_STATUS" == "200" ]]; then
        validate_response "$CAPTURED_HEADERS" "$CAPTURED_BODY" "$CAPTURED_STATUS" 200 "web root" "" 1
        break
    fi
    sleep 2
done
if [[ "${CAPTURED_STATUS:-}" != "200" ]]; then
    echo "Timed out waiting for the web container root document on port ${port}." >&2
    exit 1
fi

# Run actual HTTP requests against the NODE_ENV=production image. The auth stub
# is test-only; all requested route, middleware, matcher, and cache behavior is
# exercised by the real built Next server.
private_conversation="/chat/550e8400-e29b-41d4-a716-446655440000"
check_response anonymous-private-redirect "$private_conversation" 307 "" 0 0 "/login?next=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$private_conversation")"
check_response authenticated-private-200 "/projects" 200 "$smoke_cookie" 0 0 "" 1
check_response unknown-route-404 "/__web_container_smoke_private_cache_not_found__" 404
check_response private-rsc-200 "/projects.rsc" 200 "$smoke_cookie"

# Preserve the raw path through curl. Next must either normalize it to /projects
# before matching (the expected 307 to the normalized login return path) or return
# a private-policy 404. A policy-free API-excluded response is never accepted.
raw_path="/api/../projects"
capture_response raw-api-traversal "$raw_path" "" 0 1
case "$CAPTURED_STATUS" in
    307)
        raw_location="/login?next=%2Fprojects"
        ;;
    404)
        raw_location=""
        ;;
    *)
        echo "raw API traversal ($raw_path): expected a normalized 307 or private 404, received HTTP ${CAPTURED_STATUS:-no-status}." >&2
        exit 1
        ;;
esac
validate_response "$CAPTURED_HEADERS" "$CAPTURED_BODY" "$CAPTURED_STATUS" "$CAPTURED_STATUS" "raw API traversal ($raw_path)" "$raw_location"

echo "Web container smoke passed: production private cache policy holds for anonymous 307, authenticated 200, unknown 404, private .rsc, and raw traversal."
passed=1
