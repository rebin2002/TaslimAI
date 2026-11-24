#!/usr/bin/env bash
# Readiness-gated deployment cutover gate for the Taslim production topology.
#
# Why this exists
# ---------------
# The API applies EF Core migrations before app.Run(), so the container does not
# listen on its HTTP port until the migration chain has finished. A gateway
# request that arrives inside that window has no upstream at all: the platform
# proxy answers 502 on the application's behalf. An application 503 and a proxy
# 502 therefore mean opposite things, and a cutover gate must not merge them:
#
#   proxy 502/504 or a refused connection -> the revision has not bound yet
#   application 503 {"status":"not_ready"} -> bound, dependencies not satisfied
#   application 429                        -> probe budget exhausted (rate limiter)
#
# The gate holds traffic promotion until both services are actually serving:
#   API -> GET /health/ready  (dependency-gated readiness; never a paid call)
#   Web -> GET /              (public, unauthenticated, no provider dependency)
#
# It performs read-only GETs only. It writes nothing, mutates no platform
# setting, and never emits a credential, connection string, or provider detail.
#
# Exit codes
#   0  both gates green inside the budget: promotion to the new revision is safe
#   1  budget exhausted: do not promote; keep the previous deployment serving
#   2  usage or argument error
set -uo pipefail

API_BASE_URL="${1:-${API_BASE_URL:-}}"
WEB_BASE_URL="${2:-${WEB_BASE_URL:-}}"
BUDGET_SECONDS="${3:-${GATE_BUDGET_SECONDS:-300}}"

GATE_RETRY_SECONDS="${GATE_RETRY_SECONDS:-2}"
# A 5s default keeps the gate at 12 probes/minute per source address. The
# health rate-limit policy proposed in PR #249 (open, unmerged) caps anonymous
# health traffic at 30 requests/minute per client IP, so a tighter loop here
# would make the gate throttle itself and report a false RED. If #249 lands,
# do not lower this value below ~3s without re-checking that budget.
GATE_CONNECT_TIMEOUT_SECONDS="${GATE_CONNECT_TIMEOUT_SECONDS:-2}"
GATE_REQUEST_TIMEOUT_SECONDS="${GATE_REQUEST_TIMEOUT_SECONDS:-10}"
GATE_SKIP_WEB="${GATE_SKIP_WEB:-0}"

die() { printf '[cutover-gate] %s\n' "$1" >&2; exit 2; }

[[ -n "$API_BASE_URL" ]] || die "an API base URL is required"
if [[ "$GATE_SKIP_WEB" != "1" ]]; then
    [[ -n "$WEB_BASE_URL" ]] || die "a Web base URL is required (or set GATE_SKIP_WEB=1)"
fi
[[ "$BUDGET_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "the budget must be a positive whole number of seconds"
[[ "$GATE_RETRY_SECONDS" =~ ^[0-9]+([.][0-9]+)?$ ]] || die "GATE_RETRY_SECONDS must be non-negative"
[[ "$GATE_CONNECT_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "GATE_CONNECT_TIMEOUT_SECONDS must be positive"
[[ "$GATE_REQUEST_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "GATE_REQUEST_TIMEOUT_SECONDS must be positive"

for base in "$API_BASE_URL" "$WEB_BASE_URL"; do
    [[ -n "$base" ]] || continue
    [[ "$base" == *$'\n'* || "$base" == *$'\r'* ]] && die "a base URL contains a control character"
    [[ "$base" =~ ^https?://[^[:space:]]+$ ]] || die "base URLs must be http(s) origins: $base"
done

API_BASE_URL="${API_BASE_URL%/}"
WEB_BASE_URL="${WEB_BASE_URL%/}"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

# Classify one probe. The distinction between "nothing is listening" and
# "something is listening and refuses to be ready" is the whole point of this
# gate, so the classifier reads the terminal response body as well as the code.
classify() {
    local endpoint="$1" body_file="$2" http_code="$3" curl_rc="$4"
    python3 - "$endpoint" "$body_file" "$http_code" "$curl_rc" <<'PY'
import json
import sys

endpoint, body_file, http_code, curl_rc = sys.argv[1:5]

if curl_rc != "0":
    # 7 = refused, 28 = timed out, 52 = empty reply: none of these is an
    # application verdict, all of them mean the revision is not serving yet.
    print("no-upstream")
    sys.exit(0)

code = int(http_code) if http_code.isdigit() else -1
raw = open(body_file, "r", errors="replace").read() if body_file != "-" else ""

if code == 429:
    print("throttled")
    sys.exit(0)
if code in (502, 503, 504):
    # A gateway 502/504 carries no Taslim readiness document. An application 503
    # does, and that document is what makes it a dependency verdict instead.
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        print("no-upstream")
        sys.exit(0)
    if isinstance(payload, dict) and payload.get("service") == "Taslim API":
        print("not-ready" if payload.get("status") != "ready" else "ready")
    else:
        print("gateway-error")
    sys.exit(0)
if 200 <= code < 300:
    if endpoint in ("/health/ready", "/readiness"):
        try:
            payload = json.loads(raw)
        except json.JSONDecodeError:
            print("bad-body")
            sys.exit(0)
        checks = payload.get("checks") or []
        required = [c for c in checks if c.get("required") is True]
        if payload.get("status") != "ready" or not required:
            print("not-ready")
            sys.exit(0)
        # An advisory check (required is False) must never block promotion; this
        # is the contract issue #1028 relies on for the worker dimension.
        if any(c.get("status") != "available" for c in required):
            print("not-ready")
            sys.exit(0)
        print("ready")
    else:
        print("ready")
    sys.exit(0)
if 300 <= code < 400:
    print("redirect")
    sys.exit(0)
print(f"http-{code}")
PY
}

probe() {
    local base="$1" endpoint="$2" label="$3" body_file="$4"
    local code rc
    code="$(curl --silent --show-error --location \
        --connect-timeout "$GATE_CONNECT_TIMEOUT_SECONDS" \
        --max-time "$GATE_REQUEST_TIMEOUT_SECONDS" \
        --output "$body_file" \
        --write-out '%{http_code}' \
        "${base}${endpoint}" 2>"$TMP_DIR/${label}.curl.error")"
    rc=$?
    [[ -n "$code" ]] || code="000"
    classify "$endpoint" "$body_file" "$code" "$rc"
}

# One gate per service: retry inside the shared budget until green or exhausted.
run_gate() {
    local label="$1" base="$2" endpoint="$3" deadline budget_start outcome last attempt
    budget_start=$(date +%s)
    deadline=$((budget_start + BUDGET_SECONDS))
    attempt=0
    last=""
    while :; do
        attempt=$((attempt + 1))
        outcome="$(probe "$base" "$endpoint" "$label" "$TMP_DIR/${label}.body" || echo probe-error)"
        if [[ "$outcome" == "ready" ]]; then
            local secs=$(( $(date +%s) - budget_start ))
            printf '[cutover-gate] %s GREEN on attempt %d after %ds (%s)\n' "$label" "$attempt" "$secs" "$endpoint"
            return 0
        fi
        [[ "$outcome" != "$last" ]] && printf '[cutover-gate] %s waiting: %s (%s)\n' "$label" "$outcome" "$endpoint"
        last="$outcome"
        [[ $(date +%s) -ge $deadline ]] && break
        sleep "$GATE_RETRY_SECONDS"
    done
    printf '[cutover-gate] %s RED after %ds: last verdict %s (%s)\n' "$label" "$BUDGET_SECONDS" "$last" "$endpoint" >&2
    case "$last" in
        no-upstream)    printf '[cutover-gate] %s: the revision never bound its HTTP port inside the budget. Do not promote: the gateway is answering for the app. Check the startup migration window and the service healthcheck port.\n' "$label" >&2 ;;
        not-ready)      printf '[cutover-gate] %s: the revision is serving but a required dependency check is not available. Do not promote: investigate database/storage, not the deployment.\n' "$label" >&2 ;;
        throttled)      printf '[cutover-gate] %s: the probe budget was exhausted by the health rate limiter. Raise the interval or the per-source budget before retrying.\n' "$label" >&2 ;;
        gateway-error)  printf '[cutover-gate] %s: the gateway returned a 5xx without a Taslim readiness document. Verify the healthcheck port and the service domain.\n' "$label" >&2 ;;
        *)              printf '[cutover-gate] %s: unexpected verdict %s.\n' "$label" "$last" >&2 ;;
    esac
    return 1
}

printf '[cutover-gate] budget %ss, retry %ss, API %s, Web %s\n' \
    "$BUDGET_SECONDS" "$GATE_RETRY_SECONDS" "$API_BASE_URL" "${WEB_BASE_URL:-skipped}"

rc=0
run_gate "api" "$API_BASE_URL" "/health/ready" || rc=1
if [[ "$GATE_SKIP_WEB" != "1" ]]; then
    run_gate "web" "$WEB_BASE_URL" "/" || rc=1
fi

if (( rc == 0 )); then
    printf '[cutover-gate] PASS — readiness-gated cutover is clear for both services.\n'
else
    printf '[cutover-gate] FAIL — keep the previous deployments active and follow the recovery path.\n' >&2
fi
exit "$rc"
