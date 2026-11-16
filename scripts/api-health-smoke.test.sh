#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly script="$repo_root/scripts/api-health-smoke.sh"
readonly temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

bin_dir="$temp_dir/bin"
mkdir -p "$bin_dir"
cat > "$bin_dir/curl" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail

printf 'curl %s\n' "$*" >> "${SMOKE_COMMAND_LOG:?}"
headers=''
body=''
url=''
while (($#)); do
    case "$1" in
        --dump-header|--output|--write-out)
            [[ $# -ge 2 ]] || exit 2
            if [[ "$1" == '--dump-header' ]]; then headers="$2"; fi
            if [[ "$1" == '--output' ]]; then body="$2"; fi
            shift 2
            ;;
        http://*|https://*)
            url="$1"
            shift
            ;;
        *)
            shift
            ;;
    esac
done

endpoint="${url#http://127.0.0.1:5000}"
state_file="${SMOKE_STATE_FILE:?}"
attempt=0
if [[ -f "$state_file" ]]; then attempt="$(cat "$state_file")"; fi
attempt=$((attempt + 1))
printf '%s' "$attempt" > "$state_file"

status='200'
if [[ "$endpoint" == '/health/live' && "$attempt" == '1' && "${SMOKE_MODE:-retry}" == 'retry' ]]; then
    status='503'
fi
mode="${SMOKE_MODE:-retry}"
if [[ "$mode" == 'redirect-headers' ]]; then
    # Redirect hop carries every required header; the terminal 200 carries none.
    printf 'HTTP/1.1 302 Found\r\ncache-control: no-store\r\npragma: no-cache\r\nx-request-id: smoke-request\r\nlocation: %s/final\r\ncontent-length: 0\r\n\r\n' "$endpoint" > "$headers"
    printf 'HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: 120\r\n\r\n' >> "$headers"
elif [[ "$mode" == 'redirect-stale-status' ]]; then
    # An earlier hop is a healthy 200; the terminal response is 503.
    printf 'HTTP/1.1 200 OK\r\ncache-control: no-store\r\npragma: no-cache\r\nx-request-id: smoke-request\r\ncontent-type: application/json\r\n\r\n' > "$headers"
    printf 'HTTP/1.1 503 Service Unavailable\r\ncontent-type: application/json\r\n\r\n' >> "$headers"
elif [[ "$mode" == 'redirect-valid' ]]; then
    # Redirect hop carries nothing; the terminal 200 satisfies the whole contract.
    printf 'HTTP/1.1 302 Found\r\nlocation: %s/final\r\ncontent-length: 0\r\n\r\n' "$endpoint" > "$headers"
    printf 'HTTP/1.1 200 OK\r\ncache-control: no-store\r\npragma: no-cache\r\nx-request-id: smoke-request\r\ncontent-type: application/json\r\n\r\n' >> "$headers"
elif [[ "$mode" == 'no-block' ]]; then
    : > "$headers"
else
    printf 'HTTP/1.1 %s Test\r\ncache-control: no-store\r\npragma: no-cache\r\nx-request-id: smoke-request\r\ncontent-type: application/json\r\n\r\n' "$status" > "$headers"
fi
if [[ "${SMOKE_MODE:-retry}" == 'unsafe' && "$endpoint" == '/health/ready' ]]; then
    printf '{"status":"ready","service":"Taslim API","requestId":"smoke-request","checks":[{"name":"database","status":"available","required":true}],"password":"must-not-leak"}\n' > "$body"
elif [[ "${SMOKE_MODE:-retry}" == 'invalid' && "$endpoint" == '/health/ready' ]]; then
    printf '{"status":"ready","service":"Taslim API","requestId":"smoke-request","checks":[{"name":"storage","status":"available","required":true}]}\n' > "$body"
elif [[ "$endpoint" == '/health/live' ]]; then
    printf '{"status":"alive","service":"Taslim API","requestId":"smoke-request","checks":[{"name":"process","status":"alive","required":true}]}\n' > "$body"
else
    printf '{"status":"ready","service":"Taslim API","requestId":"smoke-request","checks":[{"name":"database","status":"available","required":true},{"name":"storage","status":"available","required":true}]}\n' > "$body"
fi
printf '%s' "$status"
STUB
cat > "$bin_dir/sleep" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$bin_dir/curl" "$bin_dir/sleep"

assert_log_contains() {
    local log_file="$1"
    local expected="$2"
    grep -F -- "$expected" "$log_file" >/dev/null || {
        echo "Expected command log entry was not found: $expected" >&2
        cat "$log_file" >&2
        exit 1
    }
}

success_log="$temp_dir/success.log"
PATH="$bin_dir:$PATH" \
SMOKE_COMMAND_LOG="$success_log" \
SMOKE_STATE_FILE="$temp_dir/success.state" \
SMOKE_MAX_ATTEMPTS=3 \
SMOKE_RETRY_SECONDS=0 \
bash "$script" http://127.0.0.1:5000

assert_log_contains "$success_log" '/health/live'
assert_log_contains "$success_log" '/health/ready'
if [[ "$(grep -c '^curl ' "$success_log")" -lt 3 ]]; then
    echo 'The smoke helper did not retry the transient liveness failure.' >&2
    cat "$success_log" >&2
    exit 1
fi

unsafe_log="$temp_dir/unsafe.log"
if PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$unsafe_log" \
    SMOKE_STATE_FILE="$temp_dir/unsafe.state" \
    SMOKE_MODE=unsafe \
    SMOKE_MAX_ATTEMPTS=1 \
    SMOKE_RETRY_SECONDS=0 \
    bash "$script" http://127.0.0.1:5000 >"$temp_dir/unsafe.output" 2>&1; then
    echo 'The smoke helper accepted a response containing secret-like diagnostic material.' >&2
    cat "$temp_dir/unsafe.output" >&2
    exit 1
fi

invalid_log="$temp_dir/invalid.log"
if PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$invalid_log" \
    SMOKE_STATE_FILE="$temp_dir/invalid.state" \
    SMOKE_MODE=invalid \
    SMOKE_MAX_ATTEMPTS=1 \
    SMOKE_RETRY_SECONDS=0 \
    bash "$script" http://127.0.0.1:5000 >"$temp_dir/invalid.output" 2>&1; then
    echo 'The smoke helper accepted an invalid HTTP 200 response on the final attempt.' >&2
    cat "$temp_dir/invalid.output" >&2
    exit 1
fi
grep -F 'returned an invalid response on attempt 1' "$temp_dir/invalid.output" >/dev/null || {
    echo 'The smoke helper did not report the invalid final-attempt response.' >&2
    cat "$temp_dir/invalid.output" >&2
    exit 1
}

# A 3xx hop must never lend its cache-policy or correlation headers to the terminal
# health document. curl --dump-header concatenates every response block, so the
# helper is required to judge the terminal block alone.
for failing_mode in redirect-headers redirect-stale-status no-block; do
    case "$failing_mode" in
        redirect-headers) expected_reason='terminal response is missing Cache-Control: no-store' ;;
        redirect-stale-status) expected_reason='terminal HTTP response status is 503, not 200' ;;
        no-block) expected_reason='no HTTP response block was captured' ;;
    esac
    mode_log="$temp_dir/$failing_mode.log"
    if PATH="$bin_dir:$PATH" \
        SMOKE_COMMAND_LOG="$mode_log" \
        SMOKE_STATE_FILE="$temp_dir/$failing_mode.state" \
        SMOKE_MODE="$failing_mode" \
        SMOKE_MAX_ATTEMPTS=1 \
        SMOKE_RETRY_SECONDS=0 \
        bash "$script" http://127.0.0.1:5000 >"$temp_dir/$failing_mode.output" 2>&1; then
        echo "The smoke helper accepted a $failing_mode response chain." >&2
        cat "$temp_dir/$failing_mode.output" >&2
        exit 1
    fi
    grep -F "$expected_reason" "$temp_dir/$failing_mode.output" >/dev/null || {
        echo "The smoke helper did not report the $failing_mode failure reason." >&2
        cat "$temp_dir/$failing_mode.output" >&2
        exit 1
    }
done

# A legitimate redirect chain whose terminal response satisfies the whole contract
# must still pass, so tightening the gate does not turn it into a false red.
valid_redirect_log="$temp_dir/redirect-valid.log"
PATH="$bin_dir:$PATH" \
SMOKE_COMMAND_LOG="$valid_redirect_log" \
SMOKE_STATE_FILE="$temp_dir/redirect-valid.state" \
SMOKE_MODE=redirect-valid \
SMOKE_MAX_ATTEMPTS=3 \
SMOKE_RETRY_SECONDS=0 \
SMOKE_REQUEST_TIMEOUT_SECONDS=5 \
bash "$script" http://127.0.0.1:5000 >"$temp_dir/redirect-valid.output" 2>&1 || {
    echo 'The smoke helper rejected a valid redirect chain with a compliant terminal response.' >&2
    cat "$temp_dir/redirect-valid.output" >&2
    exit 1
}

echo 'api-health-smoke behavior tests passed.'
