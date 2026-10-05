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
printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\n\r\n' > "$headers"
printf '<!doctype html><html><head><title>Taslim.ai</title></head><body>Taslim</body></html>\n' > "$body"
printf '200'
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
assert_log_contains '--env NODE_ENV=production'
assert_log_contains '--env PORT=3000'
assert_log_contains '--env HOSTNAME=0.0.0.0'
assert_log_contains 'http://127.0.0.1:3000/'
assert_log_contains 'docker rm --force web-container-smoke-test'

echo 'web-container-smoke script test passed.'
