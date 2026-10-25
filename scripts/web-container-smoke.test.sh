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
while (($#)); do
    case "$1" in
        --dump-header) headers="$2"; shift 2 ;;
        --output) body="$2"; shift 2 ;;
        --write-out) shift 2 ;;
        *) shift ;;
    esac
done
if [[ "${SMOKE_REDIRECT:-0}" == "1" ]]; then
    printf 'HTTP/1.1 301 Moved Permanently\r\ncontent-type: text/html\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\nlocation: /final\r\n\r\nHTTP/1.1 200 OK\r\n\r\n' > "$headers"
else
    printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\n\r\n' > "$headers"
fi
printf '<!doctype html><html><head><title>Taslim.ai</title></head><body>Taslim</body></html>\n' > "$body"
printf '200'
STUB
cat > "$bin_dir/sleep" <<'STUB'
#!/usr/bin/env bash
exec /usr/bin/sleep "$1"
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
assert_log_contains '--env NODE_ENV=production'
assert_log_contains '--env PORT=3000'
assert_log_contains '--env HOSTNAME=0.0.0.0'
assert_log_contains 'http://127.0.0.1:3000/'
assert_log_contains 'docker rm --force web-container-smoke-test'

redirect_output="$tmp_dir/redirect-output.log"
set +e
PATH="$bin_dir:$PATH" \
SMOKE_COMMAND_LOG="$log_file" \
SMOKE_REDIRECT=1 \
CONTAINER_NAME=web-container-smoke-redirect-test \
bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 1 >"$redirect_output" 2>&1
redirect_status=$?
set -e
if (( redirect_status == 0 )); then
    echo 'Expected redirect response with missing terminal headers to fail.' >&2
    cat "$redirect_output" >&2
    exit 1
fi
grep -F 'web root did not return an HTML content type' "$redirect_output" >/dev/null || {
    echo 'Expected terminal-response header validation failure was not observed.' >&2
    cat "$redirect_output" >&2
    exit 1
}

echo 'web-container-smoke script test passed.'
