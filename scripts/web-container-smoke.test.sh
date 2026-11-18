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

# A compliant terminal response: every policy header the contract asks for, in the
# block that actually produced the body.
write_compliant_terminal_block() {
    printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\ncache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow, noarchive, nosnippet\r\n\r\n'
}

# The same document as served by a production revision without the SSR cache
# fence: the framework prerender lifetime and no indexing refusal. The
# coherence contract is keyed on the no-store signal, so this response must
# stay green; the gate only tightens once a fenced revision starts answering.
write_prefence_terminal_block() {
    printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\ncache-control: public, max-age=31536000, s-maxage=31536000\r\n\r\n'
}

# A fenced document with one contract clause mutated: a shared or positive
# lifetime surviving alongside no-store, the indexing refusal dropped, or a
# malformed max-age. Each must turn the gate red on the mutated clause alone.
write_fenced_variant() {
    local cache_control="$1"
    local robots_tag="$2"
    printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\ncache-control: %s\r\n' "$cache_control"
    if [[ -n "$robots_tag" ]]; then
        printf 'x-robots-tag: %s\r\n' "$robots_tag"
    fi
    printf '\r\n'
}

mode="${SMOKE_MODE:-happy}"
status='200'
case "$mode" in
    happy)
        write_compliant_terminal_block > "$headers"
        ;;
    pre-fence)
        write_prefence_terminal_block > "$headers"
        ;;
    fenced-shared-lifetime)
        write_fenced_variant 'private, no-store, max-age=0, s-maxage=60' 'noindex, nofollow' > "$headers"
        ;;
    fenced-public)
        write_fenced_variant 'public, no-store, max-age=0' 'noindex, nofollow' > "$headers"
        ;;
    fenced-positive-maxage)
        write_fenced_variant 'private, no-cache, no-store, max-age=60' 'noindex, nofollow' > "$headers"
        ;;
    fenced-unparseable-maxage)
        write_fenced_variant 'private, no-store, max-age=abc' 'noindex, nofollow' > "$headers"
        ;;
    fenced-robots-missing)
        write_fenced_variant 'private, no-cache, no-store, must-revalidate, max-age=0' '' > "$headers"
        ;;
    redirect-headers)
        # Redirect hop carries every security header; the terminal 200 carries none.
        printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\ncontent-length: 0\r\n\r\n' > "$headers"
        printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncontent-length: 64\r\n\r\n' >> "$headers"
        ;;
    redirect-partial)
        # Terminal response supplies the sniffing header only; X-Frame-Options comes
        # exclusively from the hop, so it must not be credited to the served document.
        printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\nx-frame-options: DENY\r\ncontent-length: 0\r\n\r\n' > "$headers"
        printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\n\r\n' >> "$headers"
        ;;
    redirect-valid)
        # Redirect hop carries nothing; the terminal 200 satisfies the whole contract.
        printf 'HTTP/1.1 301 Moved Permanently\r\nlocation: https://app.example.test/\r\ncontent-length: 0\r\n\r\n' > "$headers"
        write_compliant_terminal_block >> "$headers"
        ;;
    redirect-stale-status)
        # An earlier hop is a compliant 200; the terminal response is a 503.
        printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\n\r\n' > "$headers"
        printf 'HTTP/1.1 503 Service Unavailable\r\ncontent-type: text/html; charset=utf-8\r\n\r\n' >> "$headers"
        status='503'
        ;;
    no-block)
        # curl produced no response block at all.
        : > "$headers"
        ;;
    unparseable-status)
        printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-content-type-options: nosniff\r\nx-frame-options: DENY\r\n\r\n' > "$headers"
        printf 'HTTP/1.1 - -\r\n\r\n' >> "$headers"
        ;;
    *)
        echo "unknown SMOKE_MODE: $mode" >&2
        exit 2
        ;;
esac
printf '<!doctype html><html><head><title>Taslim.ai</title></head><body>Taslim</body></html>\n' > "$body"
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
assert_log_contains '--env NODE_ENV=production'
assert_log_contains '--env PORT=3000'
assert_log_contains '--env HOSTNAME=0.0.0.0'
assert_log_contains 'http://127.0.0.1:3000/'
assert_log_contains 'docker rm --force web-container-smoke-test'

# The web and API production containers are smoke-tested from one revision, so the
# web gate must judge the terminal response alone, exactly as the API health gate
# does. Headers borrowed from an intermediate hop describe a redirect, not the HTML
# document a browser receives, and must never turn this gate green.
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
    'web root terminal response is missing X-Content-Type-Options: nosniff'
expect_rejected 'redirect-partial' \
    'web root terminal response is missing X-Frame-Options: DENY'
expect_rejected 'no-block' \
    'web root: no HTTP response block was captured'
expect_rejected 'unparseable-status' \
    'web root: terminal HTTP status line is not parseable'
expect_rejected 'redirect-stale-status' \
    'Timed out waiting for the web container root document on port 3000.'
# Once a revision answers the root document with the fenced no-store policy,
# the coherence contract must refuse every partial-fence variant: a shared
# lifetime (s-maxage or public) or a positive max-age surviving alongside
# no-store, an unparseable max-age, or a dropped indexing refusal.
expect_rejected 'fenced-shared-lifetime' \
    'web root private document must not advertise s-maxage alongside no-store'
expect_rejected 'fenced-public' \
    'web root private document must not advertise public alongside no-store'
expect_rejected 'fenced-positive-maxage' \
    'web root private document must not advertise a positive max-age alongside no-store'
expect_rejected 'fenced-unparseable-maxage' \
    'web root private document max-age is not an integer'
expect_rejected 'fenced-robots-missing' \
    'web root private document is missing X-Robots-Tag: noindex alongside no-store'

# A legitimate redirect chain whose terminal response satisfies the whole contract
# must still pass, so tightening the gate does not turn it into a false red.
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
grep -F 'Web container smoke passed' "$tmp_dir/redirect-valid.output" >/dev/null || {
    echo 'The web smoke gate did not report success for the compliant redirect chain.' >&2
    cat "$tmp_dir/redirect-valid.output" >&2
    exit 1
}

# The pre-fence production response (framework prerender lifetime, no no-store
# signal, no indexing refusal) must stay green, so this gate cannot be blamed
# for revisions that predate the SSR cache fence it pins.
if ! PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$tmp_dir/pre-fence.log" \
    SMOKE_MODE=pre-fence \
    CONTAINER_NAME=web-container-smoke-pre-fence \
    bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 5 \
    >"$tmp_dir/pre-fence.output" 2>&1; then
    echo 'The web smoke gate rejected the pre-fence production response.' >&2
    cat "$tmp_dir/pre-fence.output" >&2
    exit 1
fi
grep -F 'Web container smoke passed' "$tmp_dir/pre-fence.output" >/dev/null || {
    echo 'The web smoke gate did not report success for the pre-fence response.' >&2
    cat "$tmp_dir/pre-fence.output" >&2
    exit 1
}

echo 'web-container-smoke script test passed.'
