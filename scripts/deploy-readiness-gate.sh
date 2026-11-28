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
#   proxy 502/504 or a refused connection -> no valid application verdict
#   application 503 with a valid not_ready document -> bound, dependencies red
#   application 429                        -> probe budget exhausted (rate limiter)
#
# The gate holds traffic promotion until both services are actually serving:
#   API -> GET /health/ready  (dependency-gated readiness; never a paid call)
#   Web -> GET /              (public, unauthenticated, no provider dependency)
#
# It performs read-only GETs only. It writes nothing, mutates no platform
# setting, and never emits a credential, connection string, or provider detail.
#
# WHAT THIS GATE IS NOT
# ---------------------
# It is NOT a promotion authority on its own. Pointed at a service domain the
# platform routes that request to whichever deployment is *currently active*, so
# a green verdict proves only "an endpoint of the expected shape is answering at
# this URL", not "the replacement revision is answering". Against the public
# domain during a cutover that is a false-green: the old revision can satisfy
# every check here. See the identity rules below and docs/DEPLOYMENT_CUTOVER.md
# section 2.1.
#
# Identity rules
# --------------
#   GATE_EXPECT_SERVICE (default "Taslim API")  a readiness document must belong
#       to the Taslim API, not to any other JSON endpoint that happens to answer
#       at the path. Enforced on the 2xx path as well as the 5xx path.
#   GATE_EXPECT_REVISION (default empty)        demand proof of which revision is
#       serving, matched against a readiness body field (revision, buildSha,
#       gitCommit, version) or a response header (x-taslim-revision, x-revision,
#       x-build-sha, x-git-sha, x-commit-sha). The value must be the exact full
#       40-character hexadecimal commit SHA; prefixes are never accepted. Unset means identity is NOT
#       verified and the PASS line says so out loud. The shipped API exposes no
#       such field or header today, so setting this against production yields
#       identity-unavailable rather than a false green.
#
# Exit codes
#   0  both requested probes are green inside the budget. This is a probe result,
#      not deployment authority on a shared service domain: promotion still
#      requires candidate identity by isolation or by an observable revision.
#   1  shared API/Web budget exhausted or a red verdict: do not promote; keep the previous
#      deployment serving.
#   2  usage or argument error
set -uo pipefail

API_BASE_URL="${1:-${API_BASE_URL:-}}"
WEB_BASE_URL="${2:-${WEB_BASE_URL:-}}"
BUDGET_SECONDS="${3:-${GATE_BUDGET_SECONDS:-300}}"

# 5s default keeps the gate at 12 probes/minute per service, 24/minute for the
# pair. The health rate-limit policy proposed in PR #249 (open, unmerged) caps
# anonymous health traffic at 30 requests/minute per client IP, so a tighter
# loop here would make the gate throttle itself and report a false RED. Do not
# lower this below ~5s while sharing a source address with a platform health
# check or CI smoke without re-checking that budget.
GATE_RETRY_SECONDS="${GATE_RETRY_SECONDS:-5}"
GATE_CONNECT_TIMEOUT_SECONDS="${GATE_CONNECT_TIMEOUT_SECONDS:-2}"
GATE_REQUEST_TIMEOUT_SECONDS="${GATE_REQUEST_TIMEOUT_SECONDS:-10}"
GATE_SKIP_WEB="${GATE_SKIP_WEB:-0}"
GATE_EXPECT_SERVICE="${GATE_EXPECT_SERVICE-Taslim API}"
GATE_EXPECT_REVISION="${GATE_EXPECT_REVISION:-}"

die() { printf '[cutover-gate] %s\n' "$1" >&2; exit 2; }

[[ -n "$API_BASE_URL" ]] || die "an API base URL is required"
if [[ "$GATE_SKIP_WEB" != "1" ]]; then
    [[ -n "$WEB_BASE_URL" ]] || die "a Web base URL is required (or set GATE_SKIP_WEB=1)"
fi
[[ "$BUDGET_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "the budget must be a positive whole number of seconds"
[[ "$GATE_RETRY_SECONDS" =~ ^[0-9]+([.][0-9]+)?$ ]] || die "GATE_RETRY_SECONDS must be non-negative"
[[ "$GATE_CONNECT_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "GATE_CONNECT_TIMEOUT_SECONDS must be positive"
[[ "$GATE_REQUEST_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]] || die "GATE_REQUEST_TIMEOUT_SECONDS must be positive"
if [[ -n "$GATE_EXPECT_REVISION" ]]; then
    [[ "$GATE_EXPECT_REVISION" =~ ^[A-Fa-f0-9]{40}$ ]] \
        || die "GATE_EXPECT_REVISION must be a full 40-character hexadecimal commit SHA"
fi

GATE_RETRY_MILLISECONDS="$(python3 - "$GATE_RETRY_SECONDS" <<'PY'
from decimal import Decimal, ROUND_CEILING
import sys

milliseconds = (Decimal(sys.argv[1]) * 1000).to_integral_value(rounding=ROUND_CEILING)
print(int(milliseconds))
PY
)"

for base in "$API_BASE_URL" "$WEB_BASE_URL"; do
    [[ -n "$base" ]] || continue
    [[ "$base" == *$'\n'* || "$base" == *$'\r'* ]] && die "a base URL contains a control character"
    if ! python3 - "$base" <<'PY'
import sys
from urllib.parse import urlsplit

value = sys.argv[1]
try:
    parsed = urlsplit(value)
    port = parsed.port
    valid = (
        parsed.scheme in ("http", "https")
        and parsed.hostname is not None
        and parsed.username is None
        and parsed.password is None
        and parsed.path in ("", "/")
        and not parsed.query
        and not parsed.fragment
        and "?" not in value
        and "#" not in value
        and (port is None or 1 <= port <= 65535)
        and not any(ch.isspace() or ord(ch) < 0x20 or ord(ch) == 0x7f for ch in value)
    )
except ValueError:
    valid = False
sys.exit(0 if valid else 1)
PY
    then
        die "base URLs must be credential-free http(s) origins (scheme, host, optional port; no userinfo, path, query, or fragment)"
    fi
done

API_BASE_URL="${API_BASE_URL%/}"
WEB_BASE_URL="${WEB_BASE_URL%/}"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

# Classify one probe. The distinction between "nothing is listening" and
# "something is listening and refuses to be ready" is the whole point of this
# gate, so the classifier reads the terminal response body and headers as well
# as the status code. No verdict may be produced from the status code alone.
classify() {
    local endpoint="$1" body_file="$2" http_code="$3" curl_rc="$4" header_file="$5"
    python3 - "$endpoint" "$body_file" "$http_code" "$curl_rc" "$header_file" \
        "$GATE_EXPECT_SERVICE" "$GATE_EXPECT_REVISION" <<'PY'
import json
import re
import sys

endpoint, body_file, http_code, curl_rc, header_file = sys.argv[1:6]
expect_service, expect_revision = sys.argv[6], sys.argv[7]

if curl_rc != "0":
    # 7 = refused, 28 = timed out, 52 = empty reply: none of these is an
    # application verdict, all of them mean the revision is not serving yet.
    print("no-upstream")
    sys.exit(0)

code = int(http_code) if http_code.isdigit() else -1
raw = open(body_file, "r", errors="replace").read() if body_file != "-" else ""
headers = {}
try:
    for line in open(header_file, "r", errors="replace"):
        m = re.match(r"^([A-Za-z0-9_-]+)\s*:\s*(.*)$", line.rstrip("\r\n"))
        if m:
            headers[m.group(1).lower()] = m.group(2).strip()
except OSError:
    pass


def doc():
    try:
        payload = json.loads(raw)
    except (json.JSONDecodeError, ValueError):
        return None
    return payload if isinstance(payload, dict) else None


def valid_readiness(payload):
    """Validate the current API wire shape before trusting a 503 as a dependency verdict."""
    if not isinstance(payload, dict):
        return False
    if payload.get("status") not in ("ready", "not_ready"):
        return False
    if not isinstance(payload.get("service"), str) or not payload["service"].strip():
        return False
    if not isinstance(payload.get("requestId"), str) or not payload["requestId"].strip():
        return False
    checks = payload.get("checks")
    if not isinstance(checks, list) or not checks:
        return False
    for check in checks:
        if not isinstance(check, dict):
            return False
        if not isinstance(check.get("name"), str) or not check["name"].strip():
            return False
        if not isinstance(check.get("status"), str) or not check["status"].strip():
            return False
        if not isinstance(check.get("required"), bool):
            return False
    return True


def revision_from(payload):
    for key in ("revision", "buildSha", "build_sha", "gitCommit", "version"):
        value = payload.get(key) if isinstance(payload, dict) else None
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def identity(payload, verdict_if_ok):
    """Confirm the responder is who we think it is, then, if demanded, which
    revision it is. Only ever called on a would-be GREEN, so the failure modes
    are red verdicts rather than a silently promoted wrong answer."""
    if expect_revision:
        found = revision_from(payload) if payload else ""
        if not found:
            for key in ("x-taslim-revision", "x-revision", "x-build-sha",
                        "x-git-sha", "x-commit-sha"):
                if headers.get(key):
                    found = headers[key]
                    break
        if not found:
            # The operator asked for proof and the responder cannot supply it.
            # Refuse rather than guess: an unprovable identity is not a green.
            print("identity-unavailable")
            return
        if found.lower() != expect_revision.lower():
            print("identity-mismatch")
            return
    print(verdict_if_ok)


if code == 429:
    print("throttled")
    sys.exit(0)
if code in (502, 504):
    # 502/504 are gateway statuses, even if an intermediary echoes an
    # application-shaped body. They are never dependency verdicts.
    print("gateway-error")
    sys.exit(0)
if code == 503:
    # Only the application's documented 503 contract proves a dependency
    # failure. A malformed, foreign, or contradictory gateway response does not.
    payload = doc()
    if not valid_readiness(payload):
        print("gateway-error")
        sys.exit(0)
    if expect_service and payload.get("service") != expect_service:
        # Some other service answered at this path. Not this API's readiness document.
        print("gateway-error")
        sys.exit(0)
    if payload.get("status") == "ready":
        # A 5xx whose body claims ready is self-contradictory. Never allow a
        # non-2xx to produce a green, whatever the document asserts.
        print("conflict-5xx")
        sys.exit(0)
    required = [c for c in payload["checks"] if c["required"] is True]
    if not required or all(c["status"] == "available" for c in required):
        # A not_ready word without a red required check is not the API's
        # dependency-failure semantics, so retain the gateway/contract verdict.
        print("gateway-error")
        sys.exit(0)
    print("not-ready")
    sys.exit(0)
if 200 <= code < 300:
    if endpoint in ("/health/ready", "/readiness"):
        payload = doc()
        if payload is None:
            print("bad-body")
            sys.exit(0)
        if expect_service and payload.get("service") != expect_service:
            # Foreign JSON that merely looks ready must not pass. This is the
            # only place a responder identity is checkable on the green path.
            print("foreign-service")
            sys.exit(0)
        # Guards below are deliberately ONE decision each, so every one of them
        # is individually necessary: a mutation harness removes a line and the
        # suite must go red. An earlier draft merged the malformed-document and
        # empty-check-set cases into one guard, which made the "no required
        # checks" test pass even when the `or not required` clause was deleted.
        checks = payload.get("checks")
        if not isinstance(checks, list):
            # A readiness document without a checks array is contract drift, not
            # a dependency verdict: something is answering that is not shaped
            # like this API's readiness document at all.
            print("bad-body")
            sys.exit(0)
        required = [c for c in checks if isinstance(c, dict) and c.get("required") is True]
        if not required:
            # Covers checks == [] and an advisory-only list alike. Proving
            # nothing is not the same as proving "not ready", but it is equally
            # unpromotable, and this is the clause the review found unpinned.
            print("not-ready")
            sys.exit(0)
        if payload.get("status") != "ready":
            print("not-ready")
            sys.exit(0)
        # An advisory check (required is False) must never block promotion; this
        # is the contract issue #1028 relies on for the worker dimension.
        if any(c.get("status") != "available" for c in required):
            print("not-ready")
            sys.exit(0)
        identity(payload, "ready")
    else:
        # Non-readiness paths (Web "/") carry no identity document. Honour a
        # demanded revision only through a response header; otherwise the
        # caller is told plainly in the summary that identity is unverified.
        if expect_revision:
            identity(None, "ready")
        else:
            print("ready")
    sys.exit(0)
if 300 <= code < 400:
    # Reached only because the probe does not follow redirects. A redirect is
    # not a 2xx: a login redirect that lands on a 200 page would otherwise read
    # as a green container.
    print("redirect")
    sys.exit(0)
print(f"http-{code}")
PY
}

probe() {
    local base="$1" endpoint="$2" label="$3" body_file="$4" deadline_ms="$5"
    local code rc now_ms remaining_ms request_timeout_ms max_time
    now_ms="$(date +%s%3N)"
    remaining_ms=$((deadline_ms - now_ms))
    if (( remaining_ms <= 0 )); then
        printf 'budget-exhausted'
        return 0
    fi
    request_timeout_ms=$((GATE_REQUEST_TIMEOUT_SECONDS * 1000))
    (( request_timeout_ms > remaining_ms )) && request_timeout_ms=$remaining_ms
    printf -v max_time '%d.%03d' "$((request_timeout_ms / 1000))" "$((request_timeout_ms % 1000))"
    # No --location on purpose: following a redirect would let a 3xx land on a
    # 200 page and be classified as a healthy service.
    code="$(curl --silent --show-error --max-redirs 0 \
        --connect-timeout "$GATE_CONNECT_TIMEOUT_SECONDS" \
        --max-time "$max_time" \
        --dump-header "$TMP_DIR/${label}.headers" \
        --output "$body_file" \
        --write-out '%{http_code}' \
        "${base}${endpoint}" 2>"$TMP_DIR/${label}.curl.error")"
    rc=$?
    [[ -n "$code" ]] || code="000"
    now_ms="$(date +%s%3N)"
    if (( now_ms >= deadline_ms )); then
        printf 'budget-exhausted'
        return 0
    fi
    classify "$endpoint" "$body_file" "$code" "$rc" "$TMP_DIR/${label}.headers"
}

# Both service gates share one absolute end-to-end deadline. Every in-flight
# request and retry sleep is capped by the time remaining to that deadline.
run_gate() {
    local label="$1" base="$2" endpoint="$3" deadline_ms="$4"
    local gate_start_ms outcome last attempt now_ms remaining_ms sleep_ms sleep_seconds elapsed_secs
    gate_start_ms="$(date +%s%3N)"
    attempt=0
    last=""
    while :; do
        now_ms="$(date +%s%3N)"
        if (( now_ms >= deadline_ms )); then
            [[ -n "$last" ]] || last="budget-exhausted"
            printf '[cutover-gate] %s: shared API/Web end-to-end budget expired (including any in-flight request).\n' "$label" >&2
            break
        fi
        attempt=$((attempt + 1))
        outcome="$(probe "$base" "$endpoint" "$label" "$TMP_DIR/${label}.body" "$deadline_ms" || echo probe-error)"
        now_ms="$(date +%s%3N)"
        if (( now_ms >= deadline_ms )) && [[ "$outcome" == "ready" ]]; then
            outcome="budget-exhausted"
        fi
        if [[ "$outcome" == "budget-exhausted" ]]; then
            [[ -n "$last" ]] || last="budget-exhausted"
            printf '[cutover-gate] %s: shared API/Web end-to-end budget expired (including any in-flight request).\n' "$label" >&2
            break
        fi
        if [[ "$outcome" == "ready" ]]; then
            elapsed_secs=$(( (now_ms - gate_start_ms) / 1000 ))
            printf '[cutover-gate] %s GREEN on attempt %d after %ds (%s)\n' "$label" "$attempt" "$elapsed_secs" "$endpoint"
            return 0
        fi
        [[ "$outcome" != "$last" ]] && printf '[cutover-gate] %s waiting: %s (%s)\n' "$label" "$outcome" "$endpoint"
        last="$outcome"
        now_ms="$(date +%s%3N)"
        remaining_ms=$((deadline_ms - now_ms))
        if (( remaining_ms <= 0 )); then
            [[ -n "$last" ]] || last="budget-exhausted"
            printf '[cutover-gate] %s: shared API/Web end-to-end budget expired (including any in-flight request).\n' "$label" >&2
            break
        fi
        sleep_ms=$GATE_RETRY_MILLISECONDS
        (( sleep_ms > remaining_ms )) && sleep_ms=$remaining_ms
        if (( sleep_ms > 0 )); then
            printf -v sleep_seconds '%d.%03d' "$((sleep_ms / 1000))" "$((sleep_ms % 1000))"
            sleep "$sleep_seconds"
        fi
    done
    now_ms="$(date +%s%3N)"
    elapsed_secs=$(( (now_ms - gate_start_ms) / 1000 ))
    printf '[cutover-gate] %s RED after %ds (shared budget %ss): last verdict %s (%s)\n' "$label" "$elapsed_secs" "$BUDGET_SECONDS" "$last" "$endpoint" >&2
    case "$last" in
        no-upstream)          printf '[cutover-gate] %s: the revision never bound its HTTP port inside the budget. Do not promote: the gateway is answering for the app. Check the startup migration window and the service healthcheck port.\n' "$label" >&2 ;;
        not-ready)            printf '[cutover-gate] %s: the revision is serving but no required dependency check is satisfiable. Do not promote: investigate database/storage, not the deployment.\n' "$label" >&2 ;;
        budget-exhausted)     printf '[cutover-gate] %s: the shared API/Web end-to-end budget expired, including any in-flight request. Do not promote.\n' "$label" >&2 ;;
        throttled)            printf '[cutover-gate] %s: the probe budget was exhausted by the health rate limiter. Raise the interval or the per-source budget before retrying.\n' "$label" >&2 ;;
        gateway-error)        printf '[cutover-gate] %s: a 502/504 or a 503 without a valid, matching not_ready document and a red required check was returned. Do not diagnose dependencies from a gateway/contract response; verify the healthcheck port and service domain.\n' "$label" >&2 ;;
        foreign-service)      printf '[cutover-gate] %s: a responder that is not "%s" answered the readiness path. Wrong URL, wrong vhost, or a proxy serving another app. Do not promote.\n' "$label" "$GATE_EXPECT_SERVICE" >&2 ;;
        conflict-5xx)         printf '[cutover-gate] %s: a 5xx carrying a document that claims ready. Self-contradictory response; a non-2xx is never promotable. Do not promote.\n' "$label" >&2 ;;
        identity-mismatch)    printf '[cutover-gate] %s: the responder identifies a revision other than the expected one. The replacement is NOT serving. Do not promote.\n' "$label" >&2 ;;
        identity-unavailable) printf '[cutover-gate] %s: identity was demanded but the responder exposes no revision field or header, so it cannot be proven. Do not treat this as green. There is no per-deployment URL to fall back on: Railway domains attach to a service within an environment, not to a deployment (https://docs.railway.com/environments), so prove identity either by ISOLATION - point this gate at an isolated staging or PR environment whose service domain has exactly one candidate - or by DISCLOSURE - land the revision field/header first. See docs/DEPLOYMENT_CUTOVER.md section 2.1.\n' "$label" >&2 ;;
        bad-body)             printf '[cutover-gate] %s: a 2xx readiness document that is not shaped like one (no checks array). Contract drift or a responder that is not this API. Do not promote.\n' "$label" >&2 ;;
        *)                    printf '[cutover-gate] %s: unexpected verdict %s.\n' "$label" "$last" >&2 ;;
    esac
    return 1
}

SHARED_BUDGET_START_MS="$(date +%s%3N)"
SHARED_DEADLINE_MS=$((SHARED_BUDGET_START_MS + BUDGET_SECONDS * 1000))

web_scope="enabled"
[[ "$GATE_SKIP_WEB" == "1" ]] && web_scope="skipped"
printf '[cutover-gate] shared end-to-end budget %ss, retry %ss, API enabled, Web %s\n' \
    "$BUDGET_SECONDS" "$GATE_RETRY_SECONDS" "$web_scope"

rc=0
run_gate "api" "$API_BASE_URL" "/health/ready" "$SHARED_DEADLINE_MS" || rc=1
if [[ "$GATE_SKIP_WEB" != "1" ]]; then
    run_gate "web" "$WEB_BASE_URL" "/" "$SHARED_DEADLINE_MS" || rc=1
fi

if (( rc == 0 )); then
    if [[ -n "$GATE_EXPECT_REVISION" ]]; then
        printf '[cutover-gate] PASS — readiness green and revision identity verified against %s.\n' "$GATE_EXPECT_REVISION"
    else
        printf '[cutover-gate] PASS (IDENTITY UNVERIFIED) — shape is green but this is NOT a promotion authority:\n'
        printf '[cutover-gate] no revision identity was demanded, so these verdicts cannot distinguish the\n'
        printf '[cutover-gate] replacement revision from the still-active previous deployment on a shared domain.\n'
        printf '[cutover-gate] Confirm which deployment is answering out of band, or set GATE_EXPECT_REVISION.\n'
    fi
else
    printf '[cutover-gate] FAIL — keep the previous deployments active and follow the recovery path.\n' >&2
fi
exit "$rc"
