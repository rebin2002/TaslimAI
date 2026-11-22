#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readme="$repo_root/README.md"

grep -F 'curl http://localhost:5000/health/live' "$readme" >/dev/null
grep -F 'curl -i http://localhost:5000/readiness' "$readme" >/dev/null
grep -F 'HTTP 200 with status=ready' "$readme" >/dev/null
grep -F 'HTTP 503 with status=not_ready' "$readme" >/dev/null
grep -F 'server-generated request ID' "$readme" >/dev/null

if grep -F '# {"status":"healthy","service":"Taslim API"}' "$readme" >/dev/null; then
    echo 'README contains the obsolete health response contract.' >&2
    exit 1
fi

echo 'README Health contract test passed.'
