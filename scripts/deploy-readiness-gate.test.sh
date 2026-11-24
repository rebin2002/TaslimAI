#!/usr/bin/env bash
# Contract tests for scripts/deploy-readiness-gate.sh.
#
# The gate is only useful if it keeps "the revision has not bound" separate from
# "the revision is bound but a dependency is down". These tests drive a real
# local HTTP server per scenario instead of stubbing curl, so the classification
# is exercised end to end. Everything binds to 127.0.0.1 and nothing external is
# contacted.
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

class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json"):
        raw = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if scenario == "ready":
            return self._send(200, json.dumps(READY))
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
expect() { # label expected_rc GATE_ARGS... env via GATE_*
    local label="$1" want="$2"; shift 2
    local out rc=0
    # `env` is used so per-scenario variables carried in GATE_EXTRA_ENV are
    # applied as real assignments rather than being parsed as the command name.
    out="$(GATE_RETRY_SECONDS=0.2 GATE_CONNECT_TIMEOUT_SECONDS=1 GATE_REQUEST_TIMEOUT_SECONDS=2 \
        env ${GATE_EXTRA_ENV:-} "$gate" "$@" 2>&1)" || rc=$?
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

# Per-invocation extra environment, cleared by each scenario that does not need it.
GATE_EXTRA_ENV=""

bash -n "$gate" || { echo "gate script is not valid bash" >&2; exit 1; }

echo "--- usage guards"
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
GATE_EXTRA_ENV="GATE_SKIP_WEB=1"
expect "API-only gate" 0 "http://127.0.0.1:${api}" "" 10
expect_out "API-only gate" "PASS"
GATE_EXTRA_ENV=""

printf '\n%d passed, %d failed\n' "$pass" "$fail"
(( fail == 0 )) || exit 1
echo "deploy-readiness-gate contract tests passed."
