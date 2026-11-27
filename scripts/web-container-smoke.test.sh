#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

bin_dir="$tmp_dir/bin"
mkdir -p "$bin_dir"
cat > "$bin_dir/docker" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf 'docker %s\n' "$*" >> "${SMOKE_COMMAND_LOG:?}"
case "${1:-}" in
    run) printf 'container-id\n' ;;
    logs|rm) ;;
    *) echo "unexpected docker command: $*" >&2; exit 1 ;;
esac
STUB
cat > "$bin_dir/curl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf 'curl %s\n' "$*" >> "${SMOKE_COMMAND_LOG:?}"
headers=''
body=''
url=''
cookie=''
follow=0
raw=0
while (($#)); do
    case "$1" in
        --dump-header) headers="$2"; shift 2 ;;
        --output) body="$2"; shift 2 ;;
        --write-out) shift 2 ;;
        --header)
            case "$2" in Cookie:*) cookie="${2#Cookie: }" ;; esac
            shift 2
            ;;
        --location) follow=1; shift ;;
        --path-as-is) raw=1; shift ;;
        http://*) url="$1"; shift ;;
        *) shift ;;
    esac
done
path="${url#http://127.0.0.1:3000}"
mode="${SMOKE_MODE:-happy}"
status='200'

write_private_block() {
    local response_status="$1"
    local content_type="$2"
    local location="${3:-}"
    local cache_mode="${4:-private}"
    printf 'HTTP/1.1 %s Smoke response\r\n' "$response_status"
    printf 'content-type: %s\r\n' "$content_type"
    printf 'x-content-type-options: nosniff\r\n'
    printf 'x-frame-options: DENY\r\n'
    if [[ -n "$location" ]]; then printf 'location: %s\r\n' "$location"; fi
    if [[ "$cache_mode" == 'private' ]]; then
        printf 'cache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\n'
        printf 'x-robots-tag: noindex, nofollow, noarchive, nosnippet\r\n'
    fi
    printf '\r\n'
}

# Existing root-chain cases retain coverage for terminal-block parsing.
if [[ "$path" == '/' ]]; then
    case "$mode" in
        happy|missing-private-policy|bad-authenticated-status|bad-unknown-status|bad-rsc-status|raw-unprotected)
            write_private_block '200 OK' 'text/html; charset=utf-8' > "$headers"
            ;;
        redirect-headers)
            printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\ncontent-length: 0\r\n\r\n' > "$headers"
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncontent-length: 64\r\n\r\n' >> "$headers"
            ;;
        redirect-headerless-terminal)
            printf 'HTTP/1.1 301 Moved Permanently\r\ncontent-type: text/html\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\nlocation: /final\r\n\r\n' > "$headers"
            printf 'HTTP/1.1 200 OK\r\n\r\n' >> "$headers"
            ;;
        redirect-partial)
            printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\nx-frame-options: DENY\r\ncontent-length: 0\r\n\r\n' > "$headers"
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\n\r\n' >> "$headers"
            ;;
        redirect-valid)
            printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\ncontent-length: 0\r\n\r\n' > "$headers"
            write_private_block '200 OK' 'text/html; charset=utf-8' >> "$headers"
            ;;
        redirect-stale-status)
            write_private_block '200 OK' 'text/html; charset=utf-8' > "$headers"
            printf 'HTTP/1.1 503 Service Unavailable\r\ncontent-type: text/html; charset=utf-8\r\n\r\n' >> "$headers"
            status='503'
            ;;
        no-block)
            : > "$headers"
            ;;
        unparseable-status)
            write_private_block '200 OK' 'text/html; charset=utf-8' > "$headers"
            printf 'HTTP/1.1 - -\r\n\r\n' >> "$headers"
            ;;
        *) echo "unknown SMOKE_MODE: $mode" >&2; exit 2 ;;
    esac
    printf '<!doctype html><html><head><title>Taslim.ai</title></head><body>Taslim</body></html>\n' > "$body"
    printf '%s' "$status"
    exit 0
fi

case "$path" in
    /chat/550e8400-e29b-41d4-a716-446655440000)
        status='307'
        write_private_block '307 Temporary Redirect' 'text/html; charset=utf-8' '/login?next=%2Fchat%2F550e8400-e29b-41d4-a716-446655440000' > "$headers"
        ;;
    /projects)
        if [[ "$mode" == 'bad-authenticated-status' ]]; then
            status='307'
            write_private_block '307 Temporary Redirect' 'text/html; charset=utf-8' '/login?next=%2Fprojects' > "$headers"
        else
            status='200'
            cache_mode='private'
            [[ "$mode" == 'missing-private-policy' ]] && cache_mode='missing'
            write_private_block '200 OK' 'text/html; charset=utf-8' '' "$cache_mode" > "$headers"
        fi
        ;;
    /__web_container_smoke_private_cache_not_found__)
        if [[ "$mode" == 'bad-unknown-status' ]]; then status='200'; else status='404'; fi
        write_private_block "$status Not Found" 'text/html; charset=utf-8' > "$headers"
        ;;
    /projects.rsc)
        if [[ "$mode" == 'bad-rsc-status' ]]; then status='307'; else status='200'; fi
        write_private_block "$status RSC" 'text/x-component; charset=utf-8' > "$headers"
        ;;
    /api/../projects)
        status='307'
        cache_mode='private'
        [[ "$mode" == 'raw-unprotected' ]] && cache_mode='missing'
        if [[ "$raw" != '1' ]]; then
            echo 'smoke test did not preserve raw path segments' >&2
            exit 1
        fi
        write_private_block '307 Temporary Redirect' 'text/html; charset=utf-8' '/login?next=%2Fprojects' "$cache_mode" > "$headers"
        ;;
    *)
        status='404'
        write_private_block '404 Not Found' 'text/html; charset=utf-8' > "$headers"
        ;;
esac
printf '<!doctype html><html><body>synthetic smoke response</body></html>\n' > "$body"
printf '%s' "$status"
STUB
cat > "$bin_dir/sleep" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$bin_dir/docker" "$bin_dir/curl" "$bin_dir/sleep"

log_file="$tmp_dir/commands.log"
PATH="$bin_dir:$PATH" \
SMOKE_COMMAND_LOG="$log_file" \
CONTAINER_NAME=web-container-smoke-test \
bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 5

assert_log_contains() {
    local expected="$1"
    grep -F -- "$expected" "$log_file" >/dev/null || {
        echo "Expected command log entry was not found: $expected" >&2
        cat "$log_file" >&2
        exit 1
    }
}

assert_log_contains 'docker run --detach --publish 3000:3000 --name web-container-smoke-test'
assert_log_contains '--mount type=bind,src='
assert_log_contains 'readonly'
assert_log_contains '--env NODE_OPTIONS=--require=/tmp/web-container-smoke-auth/auth-smoke.cjs'
assert_log_contains '--env NODE_ENV=production'
assert_log_contains '--env PORT=3000'
assert_log_contains '--env HOSTNAME=0.0.0.0'
assert_log_contains 'http://127.0.0.1:3000/'
assert_log_contains 'http://127.0.0.1:3000/chat/550e8400-e29b-41d4-a716-446655440000'
assert_log_contains 'http://127.0.0.1:3000/projects'
assert_log_contains 'Cookie: taslim.auth=web-container-smoke-synthetic-session'
assert_log_contains 'http://127.0.0.1:3000/__web_container_smoke_private_cache_not_found__'
assert_log_contains 'http://127.0.0.1:3000/projects.rsc'
assert_log_contains '--path-as-is'
assert_log_contains 'http://127.0.0.1:3000/api/../projects'
assert_log_contains 'docker rm --force web-container-smoke-test'

expect_rejected() {
    local mode="$1"
    local expected_reason="$2"
    local mode_log="$tmp_dir/$mode.log"
    local mode_out="$tmp_dir/$mode.output"
    if PATH="$bin_dir:$PATH" \
        SMOKE_COMMAND_LOG="$mode_log" \
        SMOKE_MODE="$mode" \
        CONTAINER_NAME="web-container-smoke-$mode" \
        bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 1 >"$mode_out" 2>&1; then
        echo "The web smoke gate accepted the $mode response chain." >&2
        cat "$mode_out" >&2
        exit 1
    fi
    grep -F -- "$expected_reason" "$mode_out" >/dev/null || {
        echo "The web smoke gate did not report the $mode failure reason: $expected_reason" >&2
        cat "$mode_out" >&2
        exit 1
    }
}

expect_rejected 'redirect-headers' \
    'web root: terminal response is missing X-Content-Type-Options: nosniff'
expect_rejected 'redirect-headerless-terminal' \
    'web root: terminal response did not return an HTML content type'
expect_rejected 'redirect-partial' \
    'web root: terminal response is missing X-Frame-Options: DENY'
expect_rejected 'no-block' \
    'web root: no HTTP response block was captured'
expect_rejected 'unparseable-status' \
    'web root: terminal HTTP status line is not parseable'
expect_rejected 'redirect-stale-status' \
    'Timed out waiting for the web container root document on port 3000.'
expect_rejected 'missing-private-policy' \
    'authenticated-private-200 (/projects): Cache-Control is missing required directive(s)'
expect_rejected 'bad-authenticated-status' \
    'authenticated-private-200 (/projects): terminal HTTP status is 307, expected 200'
expect_rejected 'bad-unknown-status' \
    'unknown-route-404 (/__web_container_smoke_private_cache_not_found__): terminal HTTP status is 200, expected 404'
expect_rejected 'bad-rsc-status' \
    'private-rsc-200 (/projects.rsc): terminal HTTP status is 307, expected 200'
expect_rejected 'raw-unprotected' \
    'raw API traversal (/api/../projects): Cache-Control is missing required directive(s)'

# A legitimate redirect chain whose terminal response satisfies the whole
# contract must still pass, so tightening the gate adds no false red.
valid_redirect_log="$tmp_dir/redirect-valid.log"
if ! PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$valid_redirect_log" \
    SMOKE_MODE=redirect-valid \
    CONTAINER_NAME=web-container-smoke-redirect-valid \
    bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 5 \
    >"$tmp_dir/redirect-valid.output" 2>&1; then
    echo 'The web smoke gate rejected a valid redirect chain with a compliant terminal response.' >&2
    cat "$tmp_dir/redirect-valid.output" >&2
    exit 1
fi
grep -F 'Web container smoke passed:' "$tmp_dir/redirect-valid.output" >/dev/null || {
    echo 'The web smoke gate did not report success for the compliant redirect chain.' >&2
    cat "$tmp_dir/redirect-valid.output" >&2
    exit 1
}

echo 'web-container-smoke script test passed.'
