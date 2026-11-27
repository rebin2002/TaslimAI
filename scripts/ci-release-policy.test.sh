#!/usr/bin/env bash
# Deploy-safe CI/release policy.
#
# Guards the release-critical properties that the Railway Web/API production
# path depends on:
#   1. Every workflow declares a top-level `concurrency` group, so a single
#      commit stream cannot trigger multiple simultaneous production-image
#      builds or release-evidence manifests ("avoid multiple simultaneous
#      deploys per commit").
#   2. The group is scoped per workflow (gates never cancel one another) and
#      per branch / pull-request lane.
#   3. cancel-in-progress is a conditional expression, never a hardcoded true,
#      and it must NOT supersede runs targeting refs/heads/main, so every
#      integrated main commit keeps a serialized, non-cancelled
#      release-evidence and production container-smoke run.
#
# Intentionally dependency-free (bash + grep/awk only, no YAML library) so it
# runs in the fast, offline deployment-smoke-contract job with no secrets,
# no network, and no live deployment configuration.
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail=0
err() { printf 'FAIL - %s\n' "$*" >&2; fail=1; }

# Print the top-level concurrency block: the indented lines that follow a
# column-0 `concurrency:` header up to the next column-0 key (or EOF).
extract_concurrency() {
  awk '
    /^concurrency:[[:space:]]*$/ { in_block = 1; next }
    in_block && /^[^[:space:]#]/ { in_block = 0 }
    in_block { print }
  ' "$1"
}

shopt -s nullglob
workflows=( .github/workflows/*.yml .github/workflows/*.yaml )
if [[ ${#workflows[@]} -eq 0 ]]; then
  err "No workflow files found under .github/workflows."
  echo "CI/release concurrency policy tests FAILED." >&2
  exit 1
fi

for wf in "${workflows[@]}"; do
  if ! grep -Eq '^concurrency:[[:space:]]*$' "$wf"; then
    err "$wf: missing a top-level 'concurrency:' block."
    continue
  fi

  block="$(extract_concurrency "$wf")"
  if [[ -z "$block" ]]; then
    err "$wf: concurrency block is empty."
    continue
  fi

  # 1. group present and scoped per workflow + branch/pull-request lane.
  group_line="$(grep -E '^[[:space:]]*group:' <<<"$block" | head -n1 || true)"
  if [[ -z "$group_line" ]]; then
    err "$wf: concurrency.group missing."
  else
    grep -q 'github\.workflow' <<<"$group_line" \
      || err "$wf: concurrency.group must include the workflow id so independent gates do not cancel each other."
    grep -q 'github\.head_ref' <<<"$group_line" \
      || err "$wf: concurrency.group must key on the pull-request lane (github.head_ref)."
    grep -q 'github\.ref' <<<"$group_line" \
      || err "$wf: concurrency.group must key on the push ref (github.ref)."
  fi

  # 2/3. cancel-in-progress must be a conditional expression that never supersedes main.
  cil="$(grep -E '^[[:space:]]*cancel-in-progress:' <<<"$block" | head -n1 || true)"
  if [[ -z "$cil" ]]; then
    err "$wf: concurrency.cancel-in-progress missing."
  else
    if grep -Eq 'cancel-in-progress:[[:space:]]*true[[:space:]]*$' <<<"$cil"; then
      err "$wf: cancel-in-progress must not be a hardcoded true (main runs could be superseded)."
    fi
    grep -q '\${{' <<<"$cil" \
      || err "$wf: cancel-in-progress must be a conditional expression."
    grep -Eq "github\.ref[[:space:]]*!=[[:space:]]*'refs/heads/main'" <<<"$cil" \
      || err "$wf: cancel-in-progress must exclude refs/heads/main (github.ref != 'refs/heads/main')."
  fi

  if [[ "$fail" -eq 0 ]]; then
    printf 'ok   - %s declares a deploy-safe concurrency policy\n' "$wf"
  fi
done

if [[ "$fail" -ne 0 ]]; then
  echo "CI/release concurrency policy violations detected." >&2
  exit 1
fi
echo 'CI/release concurrency policy tests passed.'
