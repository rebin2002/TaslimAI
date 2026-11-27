#!/usr/bin/env bash
# Fail closed when tracked files contain high-confidence credential literals.
#
# This is intentionally a narrow static check. It never reads deployment
# secrets, calls a provider, prints matching content, or attempts rotation.
set -Eeuo pipefail

if [[ "$#" -gt 1 ]]; then
  echo "Usage: $0 [repository-root]" >&2
  exit 2
fi

repo_root="${1:-.}"
repo_root="$(git -C "$repo_root" rev-parse --show-toplevel)"
tracked_paths="$(mktemp)"
trap 'rm -f "$tracked_paths"' EXIT

# Keep the repository scan limited to tracked files. The Python process emits
# only relative paths, line numbers, and pattern names; it never emits lines
# or matching values.
git -C "$repo_root" ls-files -z > "$tracked_paths"
python3 - "$repo_root" "$tracked_paths" <<'PY'
import re
import sys
from pathlib import Path

repo_root = Path(sys.argv[1]).resolve()
tracked = Path(sys.argv[2]).read_bytes().split(b"\0")
patterns = [
    ("private_key_block", re.compile(r"BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY")),
    ("openai_key", re.compile(r"\b" + "sk" + r"-[A-Za-z0-9_-]{20,}")),
    ("github_token", re.compile(r"\b" + "gh" + r"[pousr]_[A-Za-z0-9_]{20,}")),
    ("aws_access_key", re.compile(r"\b" + "AKIA" + r"[0-9A-Z]{16}\b")),
    ("slack_token", re.compile(r"\b" + "xo" + "x[baprs]-[0-9A-Za-z-]{20,}")),
    ("google_api_key", re.compile(r"\b" + "AI" + "za" + r"[A-Za-z0-9_-]{30,}")),
    ("sendgrid_key", re.compile(r"\b" + "SG" + r"\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}")),
]
findings = 0
for raw in tracked:
    if not raw:
        continue
    relative = Path(raw.decode("utf-8"))
    path = repo_root / relative
    if not path.is_file():
        continue
    try:
        text = path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        continue
    for line_number, line in enumerate(text.splitlines(), 1):
        for name, pattern in patterns:
            if pattern.search(line):
                print(
                    f"high-confidence credential pattern detected: {relative}:{line_number} ({name})",
                    file=sys.stderr,
                )
                findings += 1
if findings:
    print(f"Secret-hygiene audit failed: {findings} finding(s). Matching content was not printed.", file=sys.stderr)
    raise SystemExit(1)
print(f"Secret-hygiene audit passed: {sum(1 for item in tracked if item)} tracked file(s) checked.")
PY
