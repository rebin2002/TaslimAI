#!/usr/bin/env bash
# Independent negative-control QA for the release-path concurrency policies
# and the seven-gate inventory introduced by PR #1143 (current-main re-cut
# of #858), assessed at exact head af78ba05ac9b47919026b5a7653175b9a0a33697.
#
# Test-only. Nothing under .github/workflows/** is modified: every fixture
# lives under a mktemp -d tree and the real workflow files are only read.
# Intentionally dependency-free (bash + grep/awk/sed/find/diff only, no YAML
# library, no network, no secrets, no sleep) so output is byte-identical
# across reruns and hosts.
#
# Scope. scripts/ci-release-policy.test.sh (#1143) already guards the
# per-file concurrency posture with six fixtures. This guard closes the
# residual scenarios it does not prove and re-proves the four release
# properties end to end, so each is demonstrated by a fail-closed control:
#
#   S1  A PR update supersedes only earlier runs of the SAME workflow AND
#       the SAME branch/PR lane. The owner guard asserts the lane keys exist
#       but has no fixture proving it rejects a group that drops the ref
#       lane (a workflow-only group), which would let a push on one branch
#       cancel runs of every other branch. Fixture 'lane collapse' proves
#       that path fails closed; 'ref-only group' proves the converse.
#   S2  Runs targeting refs/heads/main are NEVER cancelled. Beyond the
#       hardcoded-true / ${{ true }} / static-literal postures the owner
#       already rejects, this guard adds the inverted expression fixture
#       'main-only cancel' (github.ref == 'refs/heads/main', which would
#       cancel exactly the main deployments) and requires the exclusion
#       clause verbatim in every release workflow.
#   S3  Distinct umbrella workflows can never cancel each other. Beyond the
#       per-file github.workflow keying, cancellation isolation also depends
#       on the workflows' top-level display name values being pairwise
#       distinct (github.workflow resolves to that name, so two same-named
#       compliant files share one group prefix and supersede one another).
#       The per-file guard cannot see across files; fixtures 'same-name
#       pair' (rejected) and 'distinct pair' (accepted) prove the cross-file
#       collision check fails closed and is not vacuous.
#   S4  No required seven-check gate is weakened. The seven hosted gates
#       (five jobs in Taslim CI, one in Production container smoke, one in
#       Taslim Release Evidence) must still exist with their exact display
#       names, the three workflows must still trigger on push and
#       pull_request, and the smoke-contracts job must still wire the owner
#       policy guard. Fixtures 'gate renamed', 'gate removed' and
#       'gate unwired' prove a silent rename, removal, or unwiring of any
#       gate is rejected.
#
# Wiring decision stays with the #1143 owner: this file is deliberately NOT
# referenced from .github/workflows/** here, so it cannot alter the seven
# required checks; a follow-up edit of the smoke-contracts step list can
# adopt it without changing gate names, counts, or triggers.
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail=0
err() { printf 'FAIL - %s\n' "$*" >&2; fail=1; }

RELEASE_WORKFLOWS=(
  .github/workflows/ci.yml
  .github/workflows/container-health-smoke.yml
  .github/workflows/release-evidence.yml
)

# --- helpers ---------------------------------------------------------------

# Print the top-level concurrency block of one file.
extract_concurrency() {
  awk '
    /^concurrency:[[:space:]]*$/ { in_block = 1; next; }
    in_block && /^[^[:space:]#]/ { in_block = 0; }
    in_block { print; }
  ' "$1"
}

# Print the `on:` block of one file (indented lines after a column-0 `on:`).
extract_on() {
  awk '
    /^on:[[:space:]]*$/ { in_block = 1; next; }
    /^on:[[:space:]]*\[/ { sub(/^on:[[:space:]]*/, ""); print; next; }
    in_block && /^[^[:space:]#]/ { in_block = 0; }
    in_block { print; }
  ' "$1"
}

# Print the top-level `name:` value of one workflow file.
workflow_display_name() {
  awk '/^name:/ { sub(/^name:[[:space:]]*/, ""); print; exit }' "$1"
}

# Print every hosted job display name inside the `jobs:` block, one per line.
job_display_names() {
  awk '
    /^jobs:[[:space:]]*$/ { in_jobs = 1; next; }
    in_jobs && /^[^[:space:]/#]/ { in_jobs = 0; }
    in_jobs && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { seen_job = 1; named = 0; next; }
    in_jobs && seen_job && !named && /^    name:[[:space:]]*[^[:space:]]/ {
      sub(/^    name:[[:space:]]*/, ""); print; named = 1
    }
  ' "$1"
}

# Count job keys inside the `jobs:` block of one file.
job_key_count() {
  awk '
    /^jobs:[[:space:]]*$/ { in_jobs = 1; next; }
    in_jobs && /^[^[:space:]/#]/ { in_jobs = 0; }
    in_jobs && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { c++ }
    END { print c + 0 }
  ' "$1"
}

# S1/S2/S3(per-file): print violations for one workflow file's concurrency
# posture, or nothing when it is deploy-safe.
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

  group_line="$(grep -E '^[[:space:]]*group:' <<<"$block" | head -n1 || true)"
  if [[ -z "$group_line" ]]; then
    printf 'concurrency.group missing\n'
  else
    grep -q '\${{' <<<"$group_line" \
      || printf 'concurrency.group must be an expression, not a static lane shared by every ref\n'
    grep -q 'github\.workflow' <<<"$group_line" \
      || printf 'concurrency.group must include the workflow id so independent gates do not cancel each other\n'
    grep -q 'github\.head_ref' <<<"$group_line" \
      || printf 'concurrency.group must key on the pull-request lane so PR updates supersede only the same lane\n'
    grep -q 'github\.ref' <<<"$group_line" \
      || printf 'concurrency.group must key on the push ref so branch runs stay on their own lane\n'
  fi

  cil="$(grep -E '^[[:space:]]*cancel-in-progress:' <<<"$block" | head -n1 || true)"
  if [[ -z "$cil" ]]; then
    printf 'concurrency.cancel-in-progress missing\n'
  else
    if grep -Eq 'cancel-in-progress:[[:space:]]*(true|false)[[:space:]]*$' <<<"$cil"; then
      printf 'cancel-in-progress must not be a hardcoded literal\n'
    fi
    grep -q '\${{' <<<"$cil" \
      || printf 'cancel-in-progress must be a conditional expression\n'
    grep -Eq "github\.ref[[:space:]]*!=[[:space:]]*'refs/heads/main'" <<<"$cil" \
      || printf "cancel-in-progress must exclude refs/heads/main via github.ref != 'refs/heads/main'\n"
    if grep -Eq "github\.ref[[:space:]]*==[[:space:]]*'refs/heads/main'" <<<"$cil"; then
      printf 'cancel-in-progress must not invert the clause into an equals comparison\n'
    fi
  fi

  return 0
}

# S3(cross-file): print violations for a directory of workflow files. Two
# individually compliant files sharing one display name resolve into the
# same concurrency group prefix and supersede one another.
collision_violations() {
  local dir="$1" f name seen=""
  local -a files=()
  mapfile -t files < <(find "$dir" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) | LC_ALL=C sort)
  for f in "${files[@]}"; do
    name="$(workflow_display_name "$f")"
    if [[ -n "$name" ]] && grep -Fxq "$name" <<<"$seen"; then
      printf "workflow display name '%s' is shared by more than one file: their github.workflow groups collide\n" "$name"
      return 0
    fi
    [[ -n "$name" ]] && seen+="$name"$'\n'
  done
  return 0
}

# S4: print violations for the seven-gate inventory under a checkout-style dir.
gate_inventory_violations() {
  local base="$1" wf rel total=0 names=""
  for rel in "${RELEASE_WORKFLOWS[@]}"; do
    wf="$base/$rel"
    if [[ ! -f "$wf" ]]; then
      printf 'release workflow %s is missing\n' "$rel"
      continue
    fi
    if ! extract_on "$wf" | grep -Eq '^[[:space:]]*push:'; then
      printf '%s no longer triggers on push (an integrated commit would run no gate)\n' "$rel"
    fi
    if ! extract_on "$wf" | grep -Eq '^[[:space:]]*pull_request:'; then
      printf '%s no longer triggers on pull_request (PR updates would run no gate)\n' "$rel"
    fi
    total=$(( total + $(job_key_count "$wf") ))
    names+="$(job_display_names "$wf")"$'\n'
  done

  if [[ $total -ne 7 ]]; then
    printf 'release path defines %d hosted jobs instead of the required seven\n' "$total"
  fi

  local expected actual
  expected="$(printf '%s\n' \
    'Deployment smoke helper contracts' \
    'Frontend checks' \
    'API integration tests' \
    'Browser E2E' \
    'Autopilot next-wave gate' \
    'Build production images and smoke-test API' \
    'Release evidence and safety contract' | LC_ALL=C sort)"
  actual="$(printf '%s' "$names" | sed '/^$/d' | LC_ALL=C sort)"
  if [[ "$expected" != "$actual" ]]; then
    printf 'gate display names drifted from the required seven:\n'
    diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/  /'
  fi

  # The #1143 seam: gate 1 must keep wiring the owner policy guard, or the
  # concurrency policy can silently regress while all seven checks stay green.
  if [[ -f "$base/.github/workflows/ci.yml" ]] \
     && ! awk '/^  smoke-contracts:/,/^  frontend:/' "$base/.github/workflows/ci.yml" \
          | grep -q 'scripts/ci-release-policy.test.sh'; then
    printf 'smoke-contracts no longer runs scripts/ci-release-policy.test.sh (gate 1 unwired)\n'
  fi

  return 0
}

expect_rejected() {
  local label="$1" checker="$2" target="$3" violations
  violations="$("$checker" "$target")"
  if [[ -z "$violations" ]]; then
    err "negative control '$label' was accepted: the guard no longer fails closed."
  else
    printf 'ok   - negative control %-16s rejected (%s)\n' "'$label'" "$(head -n1 <<<"$violations")"
  fi
}

expect_accepted() {
  local label="$1" checker="$2" target="$3" violations
  violations="$("$checker" "$target")"
  if [[ -n "$violations" ]]; then
    err "positive control '$label' was rejected: $(head -n1 <<<"$violations")"
  else
    printf 'ok   - positive control %-16s accepted\n' "'$label'"
  fi
}

# --- real-tree assertions (prove S1-S4 hold on this head) --------------------
for wf in "${RELEASE_WORKFLOWS[@]}"; do
  expect_accepted "policy $(basename "$wf")" policy_violations "$wf"
done
expect_accepted 'gate collision'  collision_violations .github/workflows
expect_accepted 'seven gates'     gate_inventory_violations .

# --- fixtures -----------------------------------------------------------------
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

# S1/S2 positive: individually compliant reference posture, so a guard that
# rejected everything outright could not pass its own controls.
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
expect_accepted 'compliant ref' policy_violations "$temp_dir/compliant.yml"

cat > "$temp_dir/no-concurrency.yml" <<'FIXTURE'
name: Unsafe without batching
on: [push]
permissions:
  contents: read
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

cat > "$temp_dir/hardcoded-true.yml" <<'FIXTURE'
name: Unsafe hardcoded true
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: true
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

cat > "$temp_dir/main-superseded.yml" <<'FIXTURE'
name: Unsafe main superseded
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ true }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# S2 negative: inverted clause - cancels exactly when the run targets main.
cat > "$temp_dir/main-only-cancel.yml" <<'FIXTURE'
name: Unsafe main-only cancel
on: [push]
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref == 'refs/heads/main' }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# S1 negative: workflow-only group ignores the branch/PR lane, so a push on
# any ref supersedes runs of every other ref.
cat > "$temp_dir/lane-collapse.yml" <<'FIXTURE'
name: Unsafe lane collapse
on: [push]
concurrency:
  group: ${{ github.workflow }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

# S1/S3 negative: ref-only group ignores the workflow id, so umbrella gates
# share one lane and cancel each other.
cat > "$temp_dir/ref-only-group.yml" <<'FIXTURE'
name: Unsafe ref-only group
on: [push]
concurrency:
  group: ${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
jobs:
  gate:
    runs-on: ubuntu-latest
FIXTURE

expect_rejected 'no concurrency'   policy_violations "$temp_dir/no-concurrency.yml"
expect_rejected 'hardcoded true'   policy_violations "$temp_dir/hardcoded-true.yml"
expect_rejected 'main superseded'  policy_violations "$temp_dir/main-superseded.yml"
expect_rejected 'main-only cancel' policy_violations "$temp_dir/main-only-cancel.yml"
expect_rejected 'lane collapse'    policy_violations "$temp_dir/lane-collapse.yml"
expect_rejected 'ref-only group'   policy_violations "$temp_dir/ref-only-group.yml"

# S3 cross-file negative: two individually compliant files sharing one
# display name collide in one github.workflow group.
pair_dir="$temp_dir/pair"
mkdir -p "$pair_dir"
sed 's/^name: Reference compliant workflow$/name: Taslim CI/' "$temp_dir/compliant.yml" > "$pair_dir/a.yml"
sed 's/^name: Reference compliant workflow$/name: Taslim CI/' "$temp_dir/compliant.yml" > "$pair_dir/b.yml"
expect_rejected 'same-name pair' collision_violations "$pair_dir"

# S3 cross-file positive: distinct names keep the lanes independent.
ok_dir="$temp_dir/okpair"
mkdir -p "$ok_dir"
cp "$pair_dir/a.yml" "$ok_dir/a.yml"
sed 's/^name: Taslim CI$/name: Distinct second workflow/' "$pair_dir/b.yml" > "$ok_dir/b.yml"
expect_accepted 'distinct pair' collision_violations "$ok_dir"

# S4 negatives: weaken exactly one property of a clone of the real tree.
clone_tree() { # clone_tree <dir>
  mkdir -p "$1/.github/workflows"
  cp "${RELEASE_WORKFLOWS[@]}" "$1/.github/workflows/"
}

dir_x="$temp_dir/gate-renamed"
clone_tree "$dir_x"
sed -i 's/^    name: Browser E2E$/    name: Browser E2E (experimental)/' "$dir_x/.github/workflows/ci.yml"
expect_rejected 'gate renamed' gate_inventory_violations "$dir_x"

dir_x="$temp_dir/gate-removed"
clone_tree "$dir_x"
sed -i '/^  container-health:/,$d' "$dir_x/.github/workflows/container-health-smoke.yml"
expect_rejected 'gate removed' gate_inventory_violations "$dir_x"

dir_x="$temp_dir/gate-unwired"
clone_tree "$dir_x"
sed -i '/ci-release-policy\.test\.sh/d' "$dir_x/.github/workflows/ci.yml"
expect_rejected 'gate unwired' gate_inventory_violations "$dir_x"

# --- result -----------------------------------------------------------------
if [[ "$fail" -ne 0 ]]; then
  echo 'Release concurrency QA: violations detected.' >&2
  exit 1
fi
echo 'Release concurrency QA: all four scenarios proven with fail-closed negative controls.'
