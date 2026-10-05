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

printf 'HTTP/1.1 %s Test\r\ncache-control: no-store\r\npragma: no-cache\r\nx-request-id: smoke-request\r\ncontent-type: application/json\r\n\r\n' "$status" > "$headers"
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

echo 'api-health-smoke behavior tests passed.'
