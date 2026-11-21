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
while (($#)); do
    case "$1" in
        --dump-header) headers="$2"; shift 2 ;;
        --output) body="$2"; shift 2 ;;
        --write-out) shift 2 ;;
        *)
            if [[ "$1" == http* ]]; then
                url="$1"
            fi
            shift ;;
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

# The fence probe fixture set answers the probe route (default /projects) for
# the opt-in `private-SSR-fence-required` mode. SMOKE_PROBE_MODE selects the
# response posture; SMOKE_MODE keeps steering the root document fixture.
probe_path="${WEB_SMOKE_FENCE_PROBE_PATH:-/projects}"
if [[ "$url" == *"${probe_path}" ]]; then
    probe_mode="${SMOKE_PROBE_MODE:-fence-compliant}"
    case "$probe_mode" in
        fence-compliant)
            # #1038 posture, no session: the fence stamps the auth-gate
            # redirect itself, so the probe (redirects are not followed) sees
            # the full private document policy on a 3xx gate response.
            printf 'HTTP/1.1 307 Temporary Redirect\r\nlocation: /login?next=%2Fprojects\r\ncache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow, noarchive, nosnippet\r\ncontent-length: 0\r\n\r\n' > "$headers"
            status='307'
            ;;
        fence-compliant-200)
            # #1038 posture: a served document under the fence.
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow, noarchive, nosnippet\r\n\r\n' > "$headers"
            ;;
        fence-compliant-mixedcase)
            # The fence output as emitted with mixed header and directive
            # casing; policy parsing is case-insensitive, so the posture is
            # accepted rather than falsely refused.
            printf 'HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: Private, No-Cache, No-Store, Must-Revalidate, Max-Age=0\r\nX-Robots-Tag: NOINDEX, NOFOLLOW\r\n\r\n' > "$headers"
            ;;
        control-prefence)
            # Control build (middleware.ts absent): the prerendered shell
            # answers with the framework one-year lifetime and no indexing
            # refusal. The strict mode must refuse exactly this posture.
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: public, max-age=31536000, s-maxage=31536000\r\n\r\n' > "$headers"
            ;;
        control-dynamic)
            # Control build (middleware.ts absent), dynamic gate redirect: no
            # cache policy headers accompany the response at all.
            printf 'HTTP/1.1 307 Temporary Redirect\r\nlocation: /login?next=%2Fprojects\r\ncontent-length: 0\r\n\r\n' > "$headers"
            status='307'
            ;;
        probe-no-private)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: no-cache, no-store, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-no-nostore)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-cc-absent)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-smaxage)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-store, max-age=0, s-maxage=60\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-public)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, public, no-store, max-age=0\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-positive-maxage)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-cache, no-store, max-age=60\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-unparseable-maxage)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-store, max-age=abc\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            ;;
        probe-robots-missing)
            printf 'HTTP/1.1 200 OK\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\n\r\n' > "$headers"
            ;;
        probe-404)
            # A non-existent probe route cannot vouch for the fence even when
            # the headers accompany the 404 page: fail closed.
            printf 'HTTP/1.1 404 Not Found\r\ncontent-type: text/html; charset=utf-8\r\ncache-control: private, no-cache, no-store, must-revalidate, max-age=0\r\nx-robots-tag: noindex, nofollow\r\n\r\n' > "$headers"
            status='404'
            ;;
        unreachable)
            # The probe produced no HTTP response at all.
            : > "$headers"
            status='000'
            ;;
        *)
            echo "unknown SMOKE_PROBE_MODE: $probe_mode" >&2
            exit 2
            ;;
    esac
    printf '<!doctype html><html><head><title>Taslim.ai</title></head><body>Probe</body></html>\n' > "$body"
    printf '%s' "$status"
    exit 0
fi

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

# ---------------------------------------------------------------------------
# Opt-in `private-SSR-fence-required` mode. The coherence contract above is
# keyed on no-store and therefore cannot prove the fence exists; the strict
# mode additionally probes one representative authenticated route (default
# /projects) and fails closed unless that very response carries `private` and
# `no-store` plus `X-Robots-Tag: noindex` while rejecting `public`, `s-maxage`
# and a positive or unparseable `max-age`. These fixtures pin both directions:
# it passes on the #1038 posture and refuses the control build where
# middleware.ts is absent - the discrimination `next dev` E2E cannot make.
# ---------------------------------------------------------------------------

run_strict() {
    local tag="$1"
    local root_mode="$2"
    local probe_mode="$3"
    local probe_timeout="$4"
    env PATH="$bin_dir:$PATH" \
        SMOKE_COMMAND_LOG="$tmp_dir/$tag.log" \
        SMOKE_MODE="$root_mode" \
        SMOKE_PROBE_MODE="$probe_mode" \
        WEB_SMOKE_FENCE_MODE=private-SSR-fence-required \
        CONTAINER_NAME="web-container-smoke-$tag" \
        bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 "$probe_timeout" \
        >"$tmp_dir/$tag.output" 2>&1
}

expect_strict_pass() {
    local probe_mode="$1"
    local root_mode="${2:-happy}"
    local tag="strict-pass-$probe_mode"
    if ! run_strict "$tag" "$root_mode" "$probe_mode" 5; then
        echo "The strict fence mode rejected a compliant $probe_mode probe response." >&2
        cat "$tmp_dir/$tag.output" >&2
        exit 1
    fi
    grep -F 'SSR fence confirmed on /projects' "$tmp_dir/$tag.output" >/dev/null || {
        echo "The strict fence mode did not report the fence confirmation for $probe_mode." >&2
        cat "$tmp_dir/$tag.output" >&2
        exit 1
    }
    grep -F 'http://127.0.0.1:3000/projects' "$tmp_dir/$tag.log" >/dev/null || {
        echo "The strict fence mode did not probe the authenticated route for $probe_mode." >&2
        cat "$tmp_dir/$tag.log" >&2
        exit 1
    }
}

expect_strict_rejected() {
    local probe_mode="$1"
    local expected_reason="$2"
    local root_mode="${3:-happy}"
    local tag="strict-reject-$probe_mode-$root_mode"
    if run_strict "$tag" "$root_mode" "$probe_mode" 1; then
        echo "The strict fence mode accepted the $probe_mode probe response." >&2
        cat "$tmp_dir/$tag.output" >&2
        exit 1
    fi
    grep -F -- "$expected_reason" "$tmp_dir/$tag.output" >/dev/null || {
        echo "The strict fence mode did not report the $probe_mode failure reason: $expected_reason" >&2
        cat "$tmp_dir/$tag.output" >&2
        exit 1
    }
}

expect_strict_pass 'fence-compliant'
expect_strict_pass 'fence-compliant-200'
expect_strict_pass 'fence-compliant-mixedcase'

# Control-build refusals (middleware.ts absent): the framework prerender
# lifetime on a served shell, and bare gate redirects with no policy headers
# at all. Both demonstrate the existence clause the compatibility contract is
# structurally blind to.
expect_strict_rejected 'control-prefence' \
    'Cache-Control lacks the private directive required for a per-session document; the SSR cache fence appears not to be running'
expect_strict_rejected 'control-dynamic' \
    'Cache-Control is absent; the SSR cache fence appears not to be running'

# The full pre-#1038 posture (unfenced root AND unfenced probe route) must be
# refused by the strict mode, which is why it is opt-in only.
expect_strict_rejected 'control-prefence' \
    'Cache-Control lacks the private directive required for a per-session document; the SSR cache fence appears not to be running' \
    'pre-fence'

# Each rejection clause of the strict contract, mutated in isolation.
expect_strict_rejected 'probe-no-private' \
    'Cache-Control lacks the private directive required for a per-session document'
expect_strict_rejected 'probe-no-nostore' \
    'Cache-Control lacks the no-store directive required to forbid retention'
expect_strict_rejected 'probe-cc-absent' \
    'Cache-Control is absent'
expect_strict_rejected 'probe-smaxage' \
    'Cache-Control must not advertise s-maxage for a private document'
expect_strict_rejected 'probe-public' \
    'Cache-Control must not advertise public for a private document'
expect_strict_rejected 'probe-positive-maxage' \
    'Cache-Control must not advertise a positive max-age for a private document'
expect_strict_rejected 'probe-unparseable-maxage' \
    'Cache-Control max-age is not an integer'
expect_strict_rejected 'probe-robots-missing' \
    'X-Robots-Tag is missing noindex for a private document'
expect_strict_rejected 'probe-404' \
    'terminal HTTP response status is 404, not a served document or gate response'
expect_strict_rejected 'unreachable' \
    'no HTTP status was returned; refusing to infer the SSR cache fence'

# Fail-closed configuration validation: the strict lane activates on the exact
# spelling only, and an unrecognised mode - or a probe path that cannot form a
# same-origin URL - is a hard configuration error instead of a silent fallback
# to the permissive contract.
if env PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$tmp_dir/strict-bad-mode.log" \
    SMOKE_MODE=happy \
    SMOKE_PROBE_MODE=fence-compliant \
    WEB_SMOKE_FENCE_MODE=Private-SSR-Fence-Required \
    CONTAINER_NAME=web-container-smoke-strict-bad-mode \
    bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 1 \
    >"$tmp_dir/strict-bad-mode.output" 2>&1; then
    echo 'The web smoke gate silently accepted an unrecognised fence mode name.' >&2
    cat "$tmp_dir/strict-bad-mode.output" >&2
    exit 1
fi
grep -F "fence mode must be 'compatible' or 'private-SSR-fence-required'" \
    "$tmp_dir/strict-bad-mode.output" >/dev/null || {
    echo 'The web smoke gate did not reject the unrecognised fence mode with a configuration reason.' >&2
    cat "$tmp_dir/strict-bad-mode.output" >&2
    exit 1
}

if env PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$tmp_dir/strict-bad-path.log" \
    SMOKE_MODE=happy \
    SMOKE_PROBE_MODE=fence-compliant \
    WEB_SMOKE_FENCE_MODE=private-SSR-fence-required \
    WEB_SMOKE_FENCE_PROBE_PATH=projects \
    CONTAINER_NAME=web-container-smoke-strict-bad-path \
    bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 1 \
    >"$tmp_dir/strict-bad-path.output" 2>&1; then
    echo 'The web smoke gate accepted a relative fence probe path.' >&2
    cat "$tmp_dir/strict-bad-path.output" >&2
    exit 1
fi
grep -F 'fence probe path must be an absolute URL path' \
    "$tmp_dir/strict-bad-path.output" >/dev/null || {
    echo 'The web smoke gate did not reject the relative probe path with a configuration reason.' >&2
    cat "$tmp_dir/strict-bad-path.output" >&2
    exit 1
}

# Backwards-compatibility pin for the strict contract: with the fence mode
# unset - the posture of every open pre-#1038 CI job - the pre-fence root
# response stays green even when the same revision would fail the strict probe
# (SMOKE_PROBE_MODE=control-prefence), and the gate never touches the probe
# route at all. This proves the strict lane is opt-in and cannot poison the
# existing fleet.
if ! PATH="$bin_dir:$PATH" \
    SMOKE_COMMAND_LOG="$tmp_dir/compat-no-probe.log" \
    SMOKE_MODE=pre-fence \
    SMOKE_PROBE_MODE=control-prefence \
    CONTAINER_NAME=web-container-smoke-compat-no-probe \
    bash "$root/scripts/web-container-smoke.sh" taslim-web:test 3000 5 \
    >"$tmp_dir/compat-no-probe.output" 2>&1; then
    echo 'The default compatible mode regressed on the pre-#1038 posture.' >&2
    cat "$tmp_dir/compat-no-probe.output" >&2
    exit 1
fi
if grep -F -- '/projects' "$tmp_dir/compat-no-probe.log" >/dev/null; then
    echo 'The default compatible mode must not issue fence-probe requests.' >&2
    cat "$tmp_dir/compat-no-probe.log" >&2
    exit 1
fi
grep -F 'Web container smoke passed: root document and security headers are healthy.' \
    "$tmp_dir/compat-no-probe.output" >/dev/null || {
    echo 'The default compatible mode did not report its original success line.' >&2
    cat "$tmp_dir/compat-no-probe.output" >&2
    exit 1
}

echo 'web-container-smoke script test passed.'
