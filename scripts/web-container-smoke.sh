#!/usr/bin/env bash
# Validate the production web root document, its security headers and its
# private-document cache-policy coherence.
#
# This gate verifies the same revision as the API container smoke, so both halves
# of the release evidence must judge one identical standard: the terminal HTTP
# response. A redirect hop that carries X-Content-Type-Options or X-Frame-Options
# does not vouch for the HTML document that was finally served, so those headers
# must be present on the terminal response block.
#
# The same terminal block must also satisfy the private-document cache-policy
# coherence contract: whenever the root document signals a non-shared response
# (Cache-Control carrying no-store), that response must not simultaneously
# advertise a shared or positive lifetime (s-maxage, public, or max-age > 0)
# and must carry an indexing refusal (X-Robots-Tag: noindex). Pre-fence
# production revisions answer the root document with the framework prerender
# lifetime and no no-store signal, so this contract is green on them; from the
# moment the SSR cache fence lands it pins the fenced policy end to end and
# turns any partial-fence regression red in the one hosted job that runs the
# production `next start` image, which `next dev`-mode E2E cannot observe.
#
# The coherence contract is keyed on the no-store signal, so it cannot by itself
# prove the fence EXISTS: a revision that answers the root with the framework
# prerender lifetime stays green whether the fence was never deployed or was
# silently removed. The clearly opt-in `private-SSR-fence-required` mode closes
# that gap for the post-fence posture: set
#     WEB_SMOKE_FENCE_MODE=private-SSR-fence-required
# (optionally WEB_SMOKE_FENCE_PROBE_PATH, default /projects) and the gate, once
# the root contract passes, additionally requests one representative
# authenticated document route WITHOUT following redirects and fails closed
# unless its terminal response simultaneously carries Cache-Control `private`
# and `no-store` and X-Robots-Tag `noindex`, while rejecting `public`,
# `s-maxage`, and a positive or unparseable `max-age`. A control build without
# the fence middleware answers that route with the framework lifetime (or with
# no policy headers at all) and is refused on exactly the clause the fence would
# satisfy. The default `compatible` mode is unchanged for every pre-#1038
# revision, so the strict mode must be enabled only once the fence is merged;
# an unrecognised mode name is a configuration error (exit 2), never a silent
# fallback.
set -euo pipefail

image="${1:?usage: $0 IMAGE [PORT] [TIMEOUT_SECONDS]}"
port="${2:-3000}"
timeout_seconds="${3:-120}"
container_name="${CONTAINER_NAME:-taslim-web-release-smoke}"

if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
    echo "Web container smoke port must be a valid TCP port." >&2
    exit 2
fi
if ! [[ "$timeout_seconds" =~ ^[0-9]+$ ]] || (( timeout_seconds < 1 )); then
    echo "Web container smoke timeout must be a positive number of seconds." >&2
    exit 2
fi

# Opt-in strictness selector. `compatible` (default) preserves the pre-#1038 CI
# posture exactly; only the canonical spelling activates the fence-required
# probe, so a mis-typed value fails configuration validation instead of
# silently running either lane.
fence_mode="${WEB_SMOKE_FENCE_MODE:-compatible}"
fence_probe_path="${WEB_SMOKE_FENCE_PROBE_PATH:-/projects}"
case "$fence_mode" in
    compatible) ;;
    private-SSR-fence-required)
        if [[ "$fence_probe_path" != /* || "$fence_probe_path" =~ [[:space:]] ]]; then
            echo "Web container smoke fence probe path must be an absolute URL path." >&2
            exit 2
        fi
        ;;
    *)
        echo "Web container smoke fence mode must be 'compatible' or 'private-SSR-fence-required', got: $fence_mode" >&2
        exit 2
        ;;
esac

started=0
passed=0
tmp_dir="$(mktemp -d)"
cleanup() {
    if (( started == 1 && passed == 0 )); then
        echo "Web container logs (smoke failed):" >&2
        docker logs "$container_name" >&2 || true
    fi
    if (( started == 1 )); then
        docker rm --force "$container_name" >/dev/null 2>&1 || true
    fi
    rm -rf "$tmp_dir"
}
trap cleanup EXIT

docker rm --force "$container_name" >/dev/null 2>&1 || true
docker run --detach --publish "${port}:${port}" --name "$container_name" \
    --env NODE_ENV=production \
    --env PORT="$port" \
    --env HOSTNAME=0.0.0.0 \
    "$image" >/dev/null
started=1

validate_response() {
    local headers_file="$1"
    local body_file="$2"
    python3 - "$headers_file" "$body_file" <<'PY'
import sys
from pathlib import Path

headers_path, body_path = map(Path, sys.argv[1:])
# curl --dump-header appends every response block of a redirect chain to the same
# file. The header map must therefore be built from the terminal block alone: a
# policy header carried only by an earlier 3xx hop must never satisfy the contract
# for the document the browser actually receives.
blocks: list[list[str]] = []
for line in headers_path.read_text(errors="replace").splitlines():
    if line.lower().startswith("http/"):
        blocks.append([])
    if blocks:
        blocks[-1].append(line)

if not blocks:
    raise SystemExit(
        "web root: no HTTP response block was captured; refusing to infer a safe response"
    )

headers = {}
terminal_status = None
for position, line in enumerate(blocks[-1]):
    if position == 0:
        fields = line.split()
        if len(fields) < 2 or not fields[1].isdigit():
            raise SystemExit(f"web root: terminal HTTP status line is not parseable: {line!r}")
        terminal_status = int(fields[1])
        continue
    if ":" not in line:
        continue
    name, value = line.split(":", 1)
    headers[name.lower()] = value.strip().lower()

if terminal_status != 200:
    raise SystemExit(f"web root: terminal HTTP response status is {terminal_status}, not 200")

content_type = headers.get("content-type", "")
if "text/html" not in content_type:
    raise SystemExit("web root terminal response did not return an HTML content type")
if headers.get("x-content-type-options") != "nosniff":
    raise SystemExit("web root terminal response is missing X-Content-Type-Options: nosniff")
if headers.get("x-frame-options") != "deny":
    raise SystemExit("web root terminal response is missing X-Frame-Options: DENY")
cache_control = headers.get("cache-control", "")
cache_directives = {
    part.split("=", 1)[0].strip() for part in cache_control.split(",") if part.strip()
}
cache_parameters = {
    part.split("=", 1)[0].strip(): part.split("=", 1)[1].strip()
    for part in cache_control.split(",")
    if "=" in part
}
if "no-store" in cache_directives:
    # A private document that forbids stores must not simultaneously advertise
    # a shared or positive lifetime: one dropped directive reopens the CDN
    # retention path the SSR cache fence exists to close.
    if "s-maxage" in cache_parameters:
        raise SystemExit(
            "web root private document must not advertise s-maxage "
            f"alongside no-store: {cache_control!r}"
        )
    if "public" in cache_directives:
        raise SystemExit(
            "web root private document must not advertise public "
            f"alongside no-store: {cache_control!r}"
        )
    max_age = cache_parameters.get("max-age")
    if max_age is not None:
        try:
            max_age_seconds = int(max_age)
        except ValueError:
            raise SystemExit(
                f"web root private document max-age is not an integer: {max_age!r}"
            )
        if max_age_seconds > 0:
            raise SystemExit(
                "web root private document must not advertise a positive max-age "
                f"alongside no-store: {cache_control!r}"
            )
    if "noindex" not in headers.get("x-robots-tag", ""):
        raise SystemExit(
            "web root private document is missing X-Robots-Tag: noindex "
            "alongside no-store"
        )
body = body_path.read_text(errors="replace")
if not body.strip() or "<html" not in body.lower():
    raise SystemExit("web root terminal response did not return a non-empty HTML document")
PY
}

validate_fence_probe_response() {
    local headers_file="$1"
    local probe_path="$2"
    python3 - "$headers_file" "$probe_path" <<'PY'
import sys
from pathlib import Path

headers_path, probe_path = Path(sys.argv[1]), sys.argv[2]
prefix = f"fence probe {probe_path}: "

# Same terminal-block rule as the root contract: only the block that produced
# the returned document (or gate response) may vouch for the fence. The probe
# request does not follow redirects, so this is the block the fence middleware
# stamped for the authenticated route itself.
blocks: list[list[str]] = []
for line in headers_path.read_text(errors="replace").splitlines():
    if line.lower().startswith("http/"):
        blocks.append([])
    if blocks:
        blocks[-1].append(line)

if not blocks:
    raise SystemExit(
        prefix + "no HTTP response block was captured; refusing to infer a fenced response"
    )

headers = {}
terminal_status = None
for position, line in enumerate(blocks[-1]):
    if position == 0:
        fields = line.split()
        if len(fields) < 2 or not fields[1].isdigit():
            raise SystemExit(prefix + f"terminal HTTP status line is not parseable: {line!r}")
        terminal_status = int(fields[1])
        continue
    if ":" not in line:
        continue
    name, value = line.split(":", 1)
    headers[name.lower()] = value.strip().lower()

# Fail closed on anything that is not a served document or an auth-gate
# response for the route itself: a 404 or 5xx document cannot vouch for the
# fence, whatever headers accompany it.
if not 200 <= terminal_status < 400:
    raise SystemExit(
        prefix
        + f"terminal HTTP response status is {terminal_status}, not a served document or gate response"
    )

cache_control = headers.get("cache-control", "")
if not cache_control:
    raise SystemExit(
        prefix + "Cache-Control is absent; the SSR cache fence appears not to be running"
    )
cache_directives = {
    part.split("=", 1)[0].strip() for part in cache_control.split(",") if part.strip()
}
cache_parameters = {
    part.split("=", 1)[0].strip(): part.split("=", 1)[1].strip()
    for part in cache_control.split(",")
    if "=" in part
}

# Existence clauses: the fence response is per-session (private) and never
# retainable (no-store). A control build without middleware.ts answers the
# route with the framework prerender lifetime instead and is refused here.
if "private" not in cache_directives:
    raise SystemExit(
        prefix + "Cache-Control lacks the private directive required for a per-session "
        "document; the SSR cache fence appears not to be running: "
        f"{cache_control!r}"
    )
if "no-store" not in cache_directives:
    raise SystemExit(
        prefix + "Cache-Control lacks the no-store directive required to forbid retention; "
        "the SSR cache fence appears not to be running: "
        f"{cache_control!r}"
    )

# Rejection clauses: one leaked shared or positive lifetime reopens the CDN
# retention path even alongside the required directives.
if "s-maxage" in cache_parameters:
    raise SystemExit(
        prefix + f"Cache-Control must not advertise s-maxage for a private document: {cache_control!r}"
    )
if "public" in cache_directives:
    raise SystemExit(
        prefix + f"Cache-Control must not advertise public for a private document: {cache_control!r}"
    )
max_age = cache_parameters.get("max-age")
if max_age is not None:
    try:
        max_age_seconds = int(max_age)
    except ValueError:
        raise SystemExit(prefix + f"Cache-Control max-age is not an integer: {max_age!r}")
    if max_age_seconds > 0:
        raise SystemExit(
            prefix + "Cache-Control must not advertise a positive max-age for a private "
            f"document: {cache_control!r}"
        )

# Indexing-refusal clause: a per-session document that a crawler can index is
# the leak the fence exists to prevent.
if "noindex" not in headers.get("x-robots-tag", ""):
    raise SystemExit(
        prefix + "X-Robots-Tag is missing noindex for a private document: "
        f"{headers.get('x-robots-tag', '<absent>')!r}"
    )
PY
}

headers_file="$tmp_dir/root.headers"
body_file="$tmp_dir/root.body"
deadline=$((SECONDS + timeout_seconds))
while (( SECONDS < deadline )); do
    : > "$headers_file"
    : > "$body_file"
    status="$(curl --silent --show-error --location \
        --connect-timeout 3 \
        --max-time 5 \
        --dump-header "$headers_file" \
        --output "$body_file" \
        --write-out '%{http_code}' \
        "http://127.0.0.1:${port}/" 2>/dev/null || true)"
    if [[ "$status" == "200" ]] && validate_response "$headers_file" "$body_file"; then
        if [[ "$fence_mode" == "private-SSR-fence-required" ]]; then
            # The fence probe is asserted without following redirects: the
            # fence middleware stamps the response for the requested
            # authenticated route itself, so that block - not any later hop -
            # is the one that must carry the policy. A violation is reported
            # immediately instead of being retried until the deadline; only
            # total absence of an HTTP answer counts as "not ready yet" and
            # falls through to the shared timeout, which stays fail closed.
            probe_headers="$tmp_dir/fence.headers"
            probe_body="$tmp_dir/fence.body"
            : > "$probe_headers"
            : > "$probe_body"
            probe_status="$(curl --silent --show-error \
                --connect-timeout 3 \
                --max-time 5 \
                --dump-header "$probe_headers" \
                --output "$probe_body" \
                --write-out '%{http_code}' \
                "http://127.0.0.1:${port}${fence_probe_path}" 2>/dev/null || true)"
            if [[ ! "$probe_status" =~ ^[1-5][0-9][0-9]$ ]]; then
                echo "Fence probe ${fence_probe_path}: no HTTP status was returned; refusing to infer the SSR cache fence." >&2
                exit 1
            fi
            if ! validate_fence_probe_response "$probe_headers" "$fence_probe_path"; then
                exit 1
            fi
            echo "Web container smoke passed: root document and security headers are healthy; SSR fence confirmed on ${fence_probe_path}."
            passed=1
            exit 0
        fi
        echo "Web container smoke passed: root document and security headers are healthy."
        passed=1
        exit 0
    fi
    sleep 2
done

echo "Timed out waiting for the web container root document on port ${port}." >&2
exit 1
