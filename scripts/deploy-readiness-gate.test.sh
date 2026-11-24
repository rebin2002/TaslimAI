#!/usr/bin/env bash
# Contract tests for scripts/deploy-readiness-gate.sh.
#
# The gate is only useful if it keeps "the revision has not bound" separate from
# "the revision is bound but a dependency is down", and if it refuses to call
# anything green that has not proven who it is. These tests drive a real local
# HTTP server per scenario instead of stubbing curl, so classification is
# exercised end to end. Everything binds to 127.0.0.1 and nothing external is
# contacted.
#
# Scenario groups:
#   - baseline behaviour (shape, per-service independence, usage guards)
#   - the four classifier edges reported as surviving mutants in the independent
#     review of #1277 (comment 7141321366 section 3): empty required-check set,
#     foreign service identity, 3xx redirect, and a 5xx carrying a ready body
#   - serving-revision identity: the gate is a pre-promotion assertion, not a
#     promotion authority, unless an identity was demanded and could be proven
set -Eeuo pipefail
readonly repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly gate="$repo_root/scripts/deploy-readiness-gate.sh"
readonly temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"; stop_servers' EXIT

pids=()
stop_servers() {
    for pid in "${pids[@]:-}"; do
        kill "$pid" 2>/dev/null || true
    done
    pids=()
}

free_port() { python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()'; }

# scenario server: serves a fixed response for a path
start_server() {
    local port="$1" scenario="$2"
    python3 - "$port" "$scenario" > "$temp_dir/server.$port.log" 2>&1 <<'PY' &
import http.server, json, sys
port, scenario = int(sys.argv[1]), sys.argv[2]

READY = {"status": "ready", "service": "Taslim API", "requestId": "r1",
         "checks": [{"name": "database", "status": "available", "required": True},
                    {"name": "storage", "status": "available", "required": True}]}
NOT_READY = {"status": "not_ready", "service": "Taslim API", "requestId": "r2",
             "checks": [{"name": "database", "status": "available", "required": True},
                        {"name": "storage", "status": "unavailable", "required": True}]}
ADVISORY = {"status": "ready", "service": "Taslim API", "requestId": "r3",
            "checks": [{"name": "database", "status": "available", "required": True},
                       {"name": "storage", "status": "available", "required": True},
                       {"name": "worker", "status": "degraded", "required": False}]}
REQUIRED_RED = {"status": "ready", "service": "Taslim API", "requestId": "r4",
                "checks": [{"name": "database", "status": "unavailable", "required": True}]}
# MUT-D payload: claims ready and lists no required check at all. Promotion must
# not read an empty requirement set as satisfied.
EMPTY_CHECKS = {"status": "ready", "service": "Taslim API", "requestId": "r5", "checks": []}
NO_CHECKS_KEY = {"status": "ready", "service": "Taslim API", "requestId": "r6"}
# Advisory-only payload: a NON-empty checks list in which nothing is required.
# This is what makes the "no required checks" guard individually necessary: a
# suite carrying only an empty list would still be caught by a malformed
# document guard, so deleting the required-check clause would go unnoticed.
ADVISORY_ONLY = {"status": "ready", "service": "Taslim API", "requestId": "r9",
                 "checks": [{"name": "worker", "status": "degraded", "required": False},
                            {"name": "cache", "status": "available", "required": False}]}
# MUT-E payload: the shape of readiness from a different responder.
FOREIGN = {"status": "ready", "service": "Not The Taslim API", "requestId": "r7",
           "checks": [{"name": "database", "status": "available", "required": True}]}
# MUT-G payload: a 5xx whose own document contradicts its status code.
CONFLICT = {"status": "ready", "service": "Taslim API", "requestId": "r8",
            "checks": [{"name": "database", "status": "available", "required": True}]}
# A document that says not_ready while every required check reads available. The
# status word and the checks can disagree in either direction, and both must
# block: trusting the checks alone is how a draining or half-probed instance
# gets promoted.
STATUS_RED_CHECKS_GREEN = {"status": "not_ready", "service": "Taslim API", "requestId": "r10",
                           "checks": [{"name": "database", "status": "available", "required": True}]}
# A 5xx from a responder that is not the Taslim API. This is a gateway-shaped
# failure, not our dependency verdict, and the guidance must say so.
NOT_READY_FOREIGN = {"status": "not_ready", "service": "Not The Taslim API", "requestId": "r11",
                     "checks": [{"name": "database", "status": "available", "required": True}]}
REVISION = dict(READY, revision="abc123def456")

class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json", extra_headers=None):
        raw = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(raw)))
        for key, value in (extra_headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if scenario == "ready":
            return self._send(200, json.dumps(READY))
        if scenario == "revision-body":
            return self._send(200, json.dumps(REVISION))
        if scenario == "revision-header":
            return self._send(200, json.dumps(READY),
                              extra_headers={"X-Taslim-Revision": "cafe1234deadbeef"})
        if scenario == "app-503":
            return self._send(503, json.dumps(NOT_READY))
        if scenario == "gateway-502":
            return self._send(502, "<html><body>Bad Gateway</body></html>", "text/html")
        if scenario == "throttled":
            return self._send(429, json.dumps({"error": "rate limited"}))
        if scenario == "advisory-degraded":
            return self._send(200, json.dumps(ADVISORY))
        if scenario == "required-red":
            return self._send(200, json.dumps(REQUIRED_RED))
        if scenario == "empty-checks":
            return self._send(200, json.dumps(EMPTY_CHECKS))
        if scenario == "no-checks-key":
            return self._send(200, json.dumps(NO_CHECKS_KEY))
        if scenario == "advisory-only":
            return self._send(200, json.dumps(ADVISORY_ONLY))
        if scenario == "foreign-service":
            return self._send(200, json.dumps(FOREIGN))
        if scenario == "conflict-503-ready":
            return self._send(503, json.dumps(CONFLICT))
        if scenario == "status-red-checks-green":
            return self._send(200, json.dumps(STATUS_RED_CHECKS_GREEN))
        if scenario == "app-503-foreign":
            return self._send(503, json.dumps(NOT_READY_FOREIGN))
        if scenario == "redirect":
            return self._send(302, "", "text/plain",
                              extra_headers={"Location": "https://example.invalid/elsewhere"})
        if scenario == "html":
            return self._send(200, "<!doctype html><html><body>ok</body></html>", "text/html")
        if scenario == "dead-web":
            return self._send(500, json.dumps({"error": "boom"}))
        return self._send(404, json.dumps({"error": "unmapped scenario"}))

    def log_message(self, *a):
        pass

http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
PY
    pids+=("$!")
    for _ in $(seq 1 50); do
        curl -s -o /dev/null --connect-timeout 1 "http://127.0.0.1:${port}/health/ready" && return 0
        sleep 0.1
    done
    echo "scenario server on ${port} failed to start" >&2
    cat "$temp_dir/server.$port.log" >&2 || true
    return 1
}

pass=0; fail=0
expect() { # label expected_rc GATE_ARGS... environment via CASE_ENV
    local label="$1" want="$2"; shift 2
    local out rc=0
    # `env` is used so per-scenario variables carried in CASE_ENV are
    # applied as real assignments rather than being parsed as the command name.
    out="$(GATE_RETRY_SECONDS=0.2 GATE_CONNECT_TIMEOUT_SECONDS=1 GATE_REQUEST_TIMEOUT_SECONDS=2 \
        env ${CASE_ENV:-} "$gate" "$@" 2>&1)" || rc=$?
    if [[ "$rc" == "$want" ]]; then
        printf 'ok   %s (rc=%s)\n' "$label" "$rc"; pass=$((pass+1))
    else
        printf 'FAIL %s: want rc=%s got rc=%s\n%s\n' "$label" "$want" "$rc" "$out"; fail=$((fail+1))
    fi
    LAST_OUT="$out"
}
expect_out() { # label substring
    local label="$1" needle="$2"
    if grep -qF -- "$needle" <<<"$LAST_OUT"; then
        printf 'ok   %s mentions "%s"\n' "$label" "$needle"; pass=$((pass+1))
    else
        printf 'FAIL %s: missing "%s"\n%s\n' "$label" "$needle" "$LAST_OUT"; fail=$((fail+1))
    fi
}
expect_absent() { # label substring - proves a wrong verdict never reached the log
    local label="$1" needle="$2"
    if grep -qF -- "$needle" <<<"$LAST_OUT"; then
        printf 'FAIL %s: must not contain "%s"\n%s\n' "$label" "$needle" "$LAST_OUT"; fail=$((fail+1))
    else
        printf 'ok   %s does not contain "%s"\n' "$label" "$needle"; pass=$((pass+1))
    fi
}

# Configure a scenario: one API server with the given behaviour plus a healthy
# Web server, so only the API verdict can move the result. Results land in
# CASE_ARGS/CASE_ENV as globals because starting servers must NOT run in a
# subshell - pids+=() inside $() would never reach the cleanup trap.
CASE_ARGS=(); CASE_ENV=""
api_case() { # scenario [extra_env...]
    stop_servers
    local api web scenario="$1"; shift
    api="$(free_port)"; web="$(free_port)"
    start_server "$api" "$scenario"
    start_server "$web" html
    CASE_ENV="$*"
    CASE_ARGS=("http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5)
}
# API-only variant, for cases where the Web service must not be in scope.
api_only_case() { # scenario [extra_env...]
    stop_servers
    local api scenario="$1"; shift
    api="$(free_port)"
    start_server "$api" "$scenario"
    CASE_ENV="GATE_SKIP_WEB=1 $*"
    CASE_ARGS=("http://127.0.0.1:${api}" "" 5)
}
run_case() { expect "$1" "$2" "${CASE_ARGS[@]}"; }


bash -n "$gate" || { echo "gate script is not valid bash" >&2; exit 1; }

echo "--- usage guards"
CASE_ENV=""
expect "missing API url is a usage error" 2 "" "" 5
expect_out "usage guard" "API base URL is required"

echo "--- both services healthy"
api="$(free_port)"; web="$(free_port)"
start_server "$api" ready
start_server "$web" html
expect "green path promotes" 0 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 10
expect_out "green path" "PASS"

echo "--- advisory check must not block promotion (issue #1028 contract)"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" advisory-degraded
start_server "$web" html
expect "degraded advisory stays green" 0 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 10

echo "--- required check red must block promotion"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" required-red
start_server "$web" html
expect "required check red blocks" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5
# A self-declared "ready" that carries a red required check must still block:
# the gate reads the checks, not only the status word.
expect_out "required check red" "required dependency"

echo "--- gateway 502 is classified as not-yet-bound, not as dependency failure"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" gateway-502
start_server "$web" html
expect "gateway 502 blocks promotion" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5
expect_out "gateway 502" "never bound"

echo "--- application 503 is a dependency verdict"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" app-503
start_server "$web" html
expect "app 503 blocks promotion" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5
expect_out "app 503" "required dependency"

echo "--- rate limiter verdict"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" throttled
start_server "$web" html
expect "429 blocks promotion" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5
expect_out "429" "rate limiter"

echo "--- web failure blocks on its own even when the API is green"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$api" ready
start_server "$web" dead-web
expect "web 500 blocks promotion" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5

echo "--- closed port is no-upstream, not a bad-body error"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$web" html
expect "closed API port blocks" 1 "http://127.0.0.1:${api}" "http://127.0.0.1:${web}" 5
expect_out "closed port" "never bound"

echo "--- GATE_SKIP_WEB honours an API-only check"
stop_servers
api="$(free_port)"
start_server "$api" ready
CASE_ENV="GATE_SKIP_WEB=1"
expect "API-only gate" 0 "http://127.0.0.1:${api}" "" 10
expect_out "API-only gate" "PASS"
CASE_ENV=""

# The default probe interval is a rate budget against PR #249's anonymous
# health limit, and every other scenario overrides GATE_RETRY_SECONDS to run
# fast, so nothing else would notice it drifting back to the 2s that shipped at
# the reviewed head. Pin the default itself: a closed port with a 1s budget
# still prints the banner before it goes red.
echo "--- default probe interval is 5s, not the 2s that shipped at c0156e2e"
stop_servers
api="$(free_port)"; web="$(free_port)"
start_server "$web" html
CASE_ENV="GATE_SKIP_WEB=1"
# Invoked directly rather than through expect(), because that helper overrides
# GATE_RETRY_SECONDS to run the suite fast; this assertion is about the default.
# Positional order is API, WEB, BUDGET - the web slot stays empty because
# GATE_SKIP_WEB=1, and the budget is 1s so the banner prints once and the gate
# then goes red on the closed port. Only the reported interval matters here.
out_default="$(env GATE_SKIP_WEB=1 "$gate" "http://127.0.0.1:${api}" "" 1 2>&1)" || true
if grep -qE 'budget 1s, retry 5s' <<<"$out_default"; then
    printf 'ok   default retry interval is 5s (12 probes/min)\n'; pass=$((pass+1))
else
    printf 'FAIL default retry interval: expected "budget 1s, retry 5s"\n%s\n' "$out_default"; fail=$((fail+1))
fi
CASE_ENV=""

echo
echo "--- classification edges reported as surviving mutants (review 7141321366 s3)"
# MUT-D: a ready document that lists no required check proves nothing. Without
# the `not required` guard this classifies as green.
api_case empty-checks
run_case "empty checks array blocks promotion" 1
expect_out "empty checks array" "not-ready"
api_case no-checks-key
run_case "absent checks key blocks promotion" 1
expect_out "absent checks key" "not shaped like one"
# Advisory-only is the case that pins the required-check clause on its own. The
# document is well formed and non-empty, so no other guard can catch it: an
# empty-list-only suite stayed red even with the clause deleted, which is why the
# review's MUT-D survived. A degraded advisory inside a document that DOES carry
# required checks stays green (the #1028 contract above); a document whose only
# checks are advisory proves nothing and must not be promotable.
api_case advisory-only
run_case "advisory-only checks block promotion" 1
expect_out "advisory-only checks" "no required dependency check"
expect_absent "advisory-only checks" "PASS"
# MUT-E: a foreign responder wearing the readiness shape. Without the service
# identity test this classifies as green.
api_case foreign-service
run_case "foreign service identity blocks promotion" 1
expect_out "foreign service identity" 'not "Taslim API" answered'
expect_absent "foreign service identity" "PASS"
# MUT-F: a redirect is not a 2xx. This also pins that the probe must not follow
# redirects, because following one lands on a 200 page and would read as green.
api_case redirect
run_case "302 redirect blocks promotion" 1
expect_out "302 redirect" "redirect"
# MUT-G: a 5xx whose body claims ready. A non-2xx must never be promotable.
api_case conflict-503-ready
run_case "503 with ready body blocks promotion" 1
expect_out "503 with ready body" "Self-contradictory"
expect_absent "503 with ready body" "PASS"
# The mirror image of MUT-G: a body that DENIES readiness while all its checks
# look green. Trusting the checks array alone would promote it, which is what the
# removed status-word check allowed.
api_case status-red-checks-green
run_case "status not_ready with green checks blocks" 1
expect_out "status not_ready with green checks" "no required dependency check"
# A 5xx from a foreign responder is a transport/gateway verdict, not "our
# dependency is red". Both exit 1, so this is pinned on the WORDING: sending an
# operator to investigate database/storage for a wrong vhost wastes the outage.
api_case app-503-foreign
run_case "503 from foreign responder blocks" 1
expect_out "503 from foreign responder" "Verify the healthcheck port"
expect_absent "503 from foreign responder" "no required dependency check"
stop_servers

echo
echo "--- serving-revision identity (the gate is not a promotion authority)"
# Identity is not demanded by default, so a green is a green - but it must say
# out loud that it cannot tell the replacement from the previous deployment.
api_case ready
run_case "no identity demanded stays green" 0
expect_out "no identity demanded" "IDENTITY UNVERIFIED"
expect_out "no identity demanded" "NOT a promotion authority"
# Pinned on the explanation, not just the headline: a warning that says "not an
# authority" without saying why is not actionable during an incident, and an
# earlier mutant that deleted only that middle line still passed.
expect_out "no identity demanded" "cannot distinguish the"
expect_out "no identity demanded" "still-active previous deployment"
expect_absent "no identity demanded" "revision identity verified"
# Demanding identity against a responder that exposes none must go RED rather
# than guess, which is the false-green the review flagged.
api_only_case ready GATE_EXPECT_REVISION=abc123def
run_case "demanded identity with no source blocks" 1
expect_out "demanded identity with no source" "cannot be proven"
# When a revision is observable, the demand must actually discriminate.
api_only_case revision-body GATE_EXPECT_REVISION=abc123def
run_case "revision prefix match verifies identity" 0
expect_out "revision prefix match" "revision identity verified"
expect_absent "revision prefix match" "IDENTITY UNVERIFIED"
api_only_case revision-body GATE_EXPECT_REVISION=9999fffe
run_case "revision mismatch blocks promotion" 1
expect_out "revision mismatch" "replacement is NOT serving"
# Headers count as an identity source too, so no /version endpoint is required.
api_only_case revision-header GATE_EXPECT_REVISION=cafe1234
run_case "revision header verifies identity" 0
# The demand applies to both services: a proven API does not excuse a Web
# container that cannot identify itself.
api_case revision-body GATE_EXPECT_REVISION=abc123def
run_case "proven API with unprovable web blocks" 1
stop_servers

echo
echo "--- identity demand is refused for ambiguous input"
api_only_case ready GATE_EXPECT_REVISION=x
expect "too-short revision token is a usage error" 2 "${CASE_ARGS[@]}"
stop_servers

printf '\n%d passed, %d failed\n' "$pass" "$fail"
(( fail == 0 )) || exit 1
echo "deploy-readiness-gate contract tests passed."
