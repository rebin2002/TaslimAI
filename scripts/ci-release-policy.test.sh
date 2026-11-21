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
# The same assertion function is then run against deliberately unsafe fixtures
# in a temporary directory, so the guard proves it fails closed instead of
# merely reporting that today's files happen to look correct. Nothing outside
# the repository checkout is read, and no fixture touches the real workflows.
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
    /^concurrency:[[:space:]]*$/ { in_block = 1; next; }
    in_block && /^[^[:space:]#]/ { in_block = 0; }
    in_block { print; }
  ' "$1"
}

# Print one violation per line for a single workflow file, or nothing at all
# when the file is deploy-safe. The function deliberately reports through
# stdout and never mutates the shared counter: that is what lets the same
# assertions be inverted against the negative-control fixtures below.
policy_violations() {
  local wf="$1" block group_line cil

  if ! grep -Eq '^concurrency:[[:space:]]*$' "$wf"; then
    printf "missing a top-level 'concurrency:' block\n"
    return 0
  fi

  block="$(extract_concurrency "$wf")"
  if [[ -z "$block" ]]; then
    printf 'concurrency block is empty\n'
    return 0
  fi

  # 1. group present and scoped per workflow + branch/pull-request lane.
  group_line="$(grep -E '^[[:space:]]*group:' <<<"$block" | head -n1 || true)"
  if [[ -z "$group_line" ]]; then
    printf 'concurrency.group missing\n'
  else
    grep -q 'github\.workflow' <<<"$group_line" \
      || printf 'concurrency.group must include the workflow id so independent gates do not cancel each other.\n'
    grep -q 'github\.head_ref' <<<"$group_line" \
      || printf 'concurrency.group must key on the pull-request lane (github.head_ref).\n'
    grep -q 'github\.ref' <<<"$group_line" \
      || printf 'concurrency.group must key on the push ref (github.ref).\n'
  fi

  # 2/3. cancel-in-progress must be a conditional expression that never supersedes main.
  cil="$(grep -E '^[[:space:]]*cancel-in-progress:' <<<"$block" | head -n1 || true)"
  if [[ -z "$cil" ]]; then
    printf 'concurrency.cancel-in-progress missing\n'
  else
    if grep -Eq 'cancel-in-progress:[[:space:]]*true[[:space:]]*$' <<<"$cil"; then
      printf 'cancel-in-progress must not be a hardcoded true (main runs could be superseded).\n'
    fi
    grep -q '\${{' <<<"$cil" \
      || printf 'cancel-in-progress must be a conditional expression.\n'
    grep -Eq "github\.ref[[:space:]]*!=[[:space:]]*'refs/heads/main'" <<<"$cil" \
      || printf "cancel-in-progress must exclude refs/heads/main (github.ref != 'refs/heads/main').\n"
  fi

  return 0
}

shopt -s nullglob
workflows=( .github/workflows/*.yml .github/workflows/*.yaml )
if [[ ${#workflows[@]} -eq 0 ]]; then
  err "No workflow files found under .github/workflows."
  echo "CI/release concurrency policy tests FAILED." >&2
  exit 1
fi

# --- positive assertions: every real workflow must be deploy-safe -----------
for wf in "${workflows[@]}"; do
  violations="$(policy_violations "$wf")"
  if [[ -n "$violations" ]]; then
    while IFS= read -r v; do
      err "$wf: $v"
    done <<<"$violations"
  else
    printf 'ok   - %s declares a deploy-safe concurrency policy\n' "$wf"
  fi
done

# --- negative controls: the guard must reject each unsafe posture -----------
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

# A compliant reference posture, so a guard that rejected everything outright
# could not pass its own negative controls.
cat > "$temp_dir/compliant.yml" <<'FIXTURE'
name: Reference compliant workflow
on: [push]
permissions:
  contents: read
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
jobs:
  gate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
FIXTURE

# Unsafe: no concurrency block at all, so overlapping release-path runs are legal.
cat > "$temp_dir/no-concurrency.yml" <<'FIXTURE'
name: Unsafe workflow without batching
on: [push]
permissions:
  contents: read
jobs:
  gate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
FIXTURE

# Unsafe: unconditional supersede, which can cancel an integrated main run.
cat > "$temp_dir/hardcoded-cancel.yml" <<'FIXTURE'
name: Unsafe hardcoded cancel
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: true
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# Unsafe: supersedes every lane including refs/heads/main.
cat > "$temp_dir/main-superseded.yml" <<'FIXTURE'
name: Unsafe main-superseding cancel
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ true }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# Unsafe: one shared group across workflows, so independent gates cancel one another.
cat > "$temp_dir/shared-group.yml" <<'FIXTURE'
name: Unsafe shared group
on: [push]
concurrency:
  group: ${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# Unsafe: batched but never superseded anywhere, i.e. not a conditional policy.
cat > "$temp_dir/static-false.yml" <<'FIXTURE'
name: Unsafe static cancel
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: false
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

expect_rejected() {
  local label="$1" fixture="$2" violations
  violations="$(policy_violations "$fixture")"
  if [[ -z "$violations" ]]; then
    err "negative control '$label' was accepted: the guard no longer fails closed."
  else
    printf 'ok   - negative control %-19s rejected (%s)\n' "'$label'" "$(head -n1 <<<"$violations")"
  fi
}

expect_accepted() {
  local label="$1" fixture="$2" violations
  violations="$(policy_violations "$fixture")"
  if [[ -n "$violations" ]]; then
    err "positive control '$label' was rejected: $(head -n1 <<<"$violations")"
  else
    printf 'ok   - positive control %-18s accepted\n' "'$label'"
  fi
}

expect_accepted 'compliant reference' "$temp_dir/compliant.yml"
expect_rejected 'no concurrency' "$temp_dir/no-concurrency.yml"
expect_rejected 'hardcoded true' "$temp_dir/hardcoded-cancel.yml"
expect_rejected 'main superseded' "$temp_dir/main-superseded.yml"
expect_rejected 'shared group' "$temp_dir/shared-group.yml"
expect_rejected 'static false' "$temp_dir/static-false.yml"

if [[ "$fail" -ne 0 ]]; then
  echo "CI/release concurrency policy violations detected." >&2
  exit 1
fi
echo 'CI/release concurrency policy tests passed.'
