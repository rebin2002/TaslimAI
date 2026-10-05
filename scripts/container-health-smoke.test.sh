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
if [[ "$*" == *"/health/live"* ]]; then
    printf '{"status":"alive"}\n'
else
    printf '{"status":"ready"}\n'
fi
STUB
cat > "$bin_dir/sleep" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$bin_dir/docker" "$bin_dir/curl" "$bin_dir/sleep"

log_file="$tmp_dir/commands.log"
PATH="$bin_dir:$PATH" \
SMOKE_COMMAND_LOG="$log_file" \
SMOKE_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=test;Username=test;Password=test' \
CONTAINER_NAME=container-health-smoke-test \
bash "$root/scripts/container-health-smoke.sh" taslim-api:test 8080 5

assert_log_contains() {
    local expected="$1"
    grep -F -- "$expected" "$log_file" >/dev/null || {
        echo "Expected command log entry was not found: $expected" >&2
        cat "$log_file" >&2
        exit 1
    }
}

assert_log_contains 'docker run --detach --network host --name container-health-smoke-test'
assert_log_contains '--env ASPNETCORE_ENVIRONMENT=Testing'
assert_log_contains '--env Database__ApplyMigrations=false'
assert_log_contains '--env Files__StorageProvider=Local'
assert_log_contains 'http://127.0.0.1:8080/health/live'
assert_log_contains 'http://127.0.0.1:8080/health/ready'
assert_log_contains 'docker rm --force container-health-smoke-test'

echo 'container-health-smoke script test passed.'
