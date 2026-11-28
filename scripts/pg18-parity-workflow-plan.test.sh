#!/usr/bin/env bash
# Offline regression for the workflow plan step's shell/data boundary.
# Extracts and runs only the plan shell block; no GitHub Actions or database runs.
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKFLOW="$ROOT_DIR/.github/workflows/pg18-parity.yml"
SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT
PLAN_SCRIPT="$SANDBOX/plan.sh"

python3 - "$WORKFLOW" "$PLAN_SCRIPT" <<'PY'
from pathlib import Path
import sys
workflow = Path(sys.argv[1]).read_text().splitlines()
step = next(i for i, line in enumerate(workflow)
            if line.strip() == "- name: Decide which legs this run may execute")
run = next(i for i in range(step + 1, len(workflow))
           if workflow[i].strip() == "run: |")
indent = len(workflow[run]) - len(workflow[run].lstrip()) + 2
script = []
for line in workflow[run + 1:]:
    if line.strip() and len(line) - len(line.lstrip()) < indent:
        break
    script.append(line[indent:] if line.strip() else "")
text = "\n".join(script) + "\n"
if "${{" in text:
    raise SystemExit("GitHub expression interpolation is present in the plan shell source")
for contract in (
    'printf -- \'- event: `%s` / ref: `%s`\\n\' "$EVENT" "$REF_NAME"',
    'printf -- \'- reason: %s\\n\' "$REASON"',
    'git check-ref-format --branch "$candidate_branch"',
    'guards-only | billing | autopilot | schema-diff | all',
):
    if contract not in text:
        raise SystemExit(f"missing workflow-plan safety contract: {contract}")
Path(sys.argv[2]).write_text(text)
PY

pass=0
failures=0
ok() { printf '  ok   %s\n' "$1"; pass=$((pass + 1)); }
fail() { printf '  FAIL %s\n' "$1"; failures=$((failures + 1)); }

sha=0000000000000000000000000000000000000000
marker="$SANDBOX/command-was-executed"
evil="\$(touch $marker) with spaces"
: > "$SANDBOX/output"
: > "$SANDBOX/summary"
if env EVENT=workflow_dispatch MESSAGE= CHOICE= REQUESTED= SHA="$sha" \
  REF_NAME="$evil" REASON="$evil" \
  GITHUB_OUTPUT="$SANDBOX/output" GITHUB_STEP_SUMMARY="$SANDBOX/summary" \
  bash "$PLAN_SCRIPT"; then
  if grep -Fxq "value=guards-only" "$SANDBOX/output" \
    && grep -Fxq "candidate=$sha" "$SANDBOX/output" \
    && grep -Fq -- "$evil" "$SANDBOX/summary" \
    && [[ ! -e "$marker" ]]; then
    ok "blank dispatch choice defaults to guards-only; summary values remain literal"
  else
    fail "summary/output safety contract failed"
  fi
else
  fail "safe guards-only plan invocation was refused"
fi

: > "$SANDBOX/output"
if env EVENT=workflow_dispatch MESSAGE= CHOICE=guards-only REQUESTED="$evil" SHA="$sha" \
  REF_NAME=branch REASON=offline \
  GITHUB_OUTPUT="$SANDBOX/output" GITHUB_STEP_SUMMARY="$SANDBOX/summary" \
  bash "$PLAN_SCRIPT" >/dev/null 2>&1; then
  fail "command-shaped candidate ref was accepted"
elif [[ ! -s "$SANDBOX/output" && ! -e "$marker" ]]; then
  ok "command-shaped candidate ref refused before writing workflow outputs"
else
  fail "invalid candidate ref wrote an output or executed shell text"
fi

: > "$SANDBOX/output"
if env EVENT=workflow_dispatch MESSAGE= CHOICE="$evil" REQUESTED="$sha" SHA="$sha" \
  REF_NAME=branch REASON=offline \
  GITHUB_OUTPUT="$SANDBOX/output" GITHUB_STEP_SUMMARY="$SANDBOX/summary" \
  bash "$PLAN_SCRIPT" >/dev/null 2>&1; then
  fail "command-shaped leg choice was accepted"
elif [[ ! -s "$SANDBOX/output" && ! -e "$marker" ]]; then
  ok "unsupported leg choice refused before writing workflow outputs"
else
  fail "invalid leg choice wrote an output or executed shell text"
fi

printf '\npg18-parity-workflow-plan.test.sh: %s passed, %s failed\n' "$pass" "$failures"
[[ "$failures" -eq 0 ]]
