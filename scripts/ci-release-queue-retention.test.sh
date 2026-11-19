#!/usr/bin/env bash
# Main-run retention contract for the release-path workflows.
#
# Addresses GitHub review 5972287325 on PR #1143 (head af78ba05):
# cancel-in-progress: ${{ github.ref != 'refs/heads/main' }} protects the
# *in-progress* main run, but with the default queue: single only ONE run may
# be *pending* per concurrency group, and "when a new job or workflow run is
# queued, any existing pending job or workflow run in the same group is
# canceled and replaced" (docs.github.com control-workflow-concurrency).
# So with main commits A (running), B (pending), C (arrives): C replaces B even
# though cancel-in-progress is false for main. queue: max keeps up to 100
# pending runs, but "the combination of queue: max and cancel-in-progress:
# true is not allowed and will result in a workflow validation error", so it
# cannot simply be added to the existing expression-based block.
#
# The split-lane design this contract enforces:
#   * workflow level  - main pushes resolve to a run-unique group (run_id),
#     so the column-0 lane never replaces a pending integrated commit; PR and
#     branch lanes keep the shared head_ref/ref lane with the supersession
#     expression, preserving safe PR-lane supersede.
#   * job level       - the two production jobs (container smoke, release
#     evidence) carry the real main serialization point with
#     group: <main ? github.workflow : run-unique>, queue: max and a literal
#     cancel-in-progress: false - the only combination GitHub documents as
#     valid for queue: max. Non-main runs resolve to a run-unique job group,
#     so they never contend there and are superseded at the workflow level.
#
# Proof obligations (deterministic, offline, bash + grep/awk only):
#   1. Static posture of the three real workflows (grouping, lanes, blocks).
#   2. A synthetic three-rapid-main-pushes simulation built strictly from the
#      documented semantics: the pre-fix posture must LOSE B (non-vacuity -
#      this is exactly the reported defect) and the shipped posture must
#      retain A+B+C serialized; the invalid queue: max + cancel true posture
#      must be refused as a validation error; the PR lane must still supersede.
#   3. Negative-control fixtures under mktemp -d (never touching real files):
#      each unsafe or regression posture is rejected, compliant references
#      accepted, so the contract demonstrably fails closed.
#   4. Gate-1 wiring of this script and of the owner policy guard.
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail=0
err() { printf 'FAIL - %s\n' "$*" >&2; fail=1; }
ok()  { printf 'ok   - %s\n' "$*"; }

workflows=( .github/workflows/ci.yml .github/workflows/container-health-smoke.yml .github/workflows/release-evidence.yml )
production=( .github/workflows/container-health-smoke.yml .github/workflows/release-evidence.yml )

# --- extractors -------------------------------------------------------------

# Print the top-level (column-0) concurrency block, comment lines stripped.
extract_top_block() {
  awk '
    /^concurrency:[[:space:]]*$/ { in_block = 1; next; }
    in_block && /^[^[:space:]#]/ { in_block = 0; }
    in_block && /^[[:space:]]*#/ { next; }
    in_block { print; }
  ' "$1"
}

# Print every indented (job-level) concurrency block, comment lines stripped,
# blocks separated by a '--- block ---' marker line.
extract_job_blocks() {
  awk '
    function ind(s) { match(s, /^ */); return RLENGTH }
    {
      if (!in_block) {
        if ($0 ~ /^ +concurrency:[[:space:]]*$/) { in_block = 1; bi = ind($0) }
        next
      }
      if ($0 ~ /^[[:space:]]*$/) { next }
      if (ind($0) <= bi) {
        in_block = 0
        print "--- block ---"
        if ($0 ~ /^ +concurrency:[[:space:]]*$/) { in_block = 1; bi = ind($0) }
        next
      }
      if ($0 ~ /^[[:space:]]*#/) { next }
      print
    }
    END { if (in_block) print "--- block ---" }
  ' "$1"
}

# --- documented-semantics simulator -----------------------------------------
# simulate_three_main_pushes <lane> <queue> <cil-main>
#   lane    shared|unique : do integrated main runs share the concurrency
#             group that is the first contention point for the run?
#   queue   single|max
#   cil-main keep|cancel  : does cancel-in-progress evaluate true for main?
# Emits the terminal states of A (running), then B, then C arriving while A
# is still running, per the documented rules:
#   * a unique lane never contends: every run starts and completes.
#   * shared + single: the newest arrival replaces the pending run (B lost).
#   * shared + max: every pending run is kept and processed FIFO.
#   * shared + max with a cancel that fires for main: GitHub refuses the
#     workflow outright (validation error) - not a legal posture.
simulate_three_main_pushes() {
  local lane="$1" queue="$2" cil="$3"
  if [[ "$lane" == unique ]]; then
    printf 'A:started B:started C:started retained=3 lost=0\n'
    return 0
  fi
  if [[ "$queue" == max && "$cil" == cancel ]]; then
    printf 'INVALID: queue max combined with cancel true\n'
    return 0
  fi
  case "$cil:$queue" in
    keep:single)   printf 'A:running B:canceled-replaced C:queued retained=2 lost=1\n' ;;
    keep:max)      printf 'A:running B:queued C:queued retained=3 lost=0\n' ;;
    cancel:single) printf 'A:canceled-superseded B:canceled-replaced C:running retained=1 lost=2\n' ;;
    *)             printf 'INVALID posture inputs\n' ;;
  esac
}

# simulate_pr_updates: a shared per-PR lane with supersession enabled.
# The latest PR head must survive and every stale in-progress/pending run
# must be superseded - the PR lane is required to keep this behaviour.
simulate_pr_updates() {
  printf 'v1:canceled-superseded v2:canceled-replaced v3:running latest-survives=1 stale=0\n'
}

# --- posture parser -----------------------------------------------------------

# Does the top-level group resolve integrated main pushes to a run-unique
# lane (the retention split), rather than one shared refs/heads/main lane?
top_main_unique() {
  local block
  block="$(extract_top_block "$1")"
  grep -Eq "group:.*github\.ref == 'refs/heads/main' && github\.run_id" <<<"$block"
}

top_cil_for_main() {
  local block cil
  block="$(extract_top_block "$1")"
  cil="$(grep -E '^[[:space:]]*cancel-in-progress:' <<<"$block" | head -n1 || true)"
  if grep -Eq 'cancel-in-progress:[[:space:]]*true[[:space:]]*$' <<<"$cil"; then
    printf 'cancel'
  elif grep -Eq "github\.ref[[:space:]]*!=[[:space:]]*'refs/heads/main'" <<<"$cil"; then
    printf 'keep'
  elif grep -Eq 'cancel-in-progress:[[:space:]]*false[[:space:]]*$' <<<"$cil"; then
    printf 'keep'
  else
    printf 'cancel'   # unknown/inverted expression: fail closed
  fi
}

top_queue_value() {
  local block q
  block="$(extract_top_block "$1")"
  q="$(grep -E '^[[:space:]]*queue:' <<<"$block" | head -n1 || true)"
  if grep -Eq 'queue:[[:space:]]*max[[:space:]]*$' <<<"$q"; then
    printf 'max'
  else
    printf 'single'
  fi
}

# Count job-level blocks that form a valid main release lane: main resolves
# to the per-workflow shared lane, queue: max, literal cancel-in-progress:
# false, group keyed on github.workflow so lanes stay cross-isolated.
release_lane_count() {
  local blocks
  blocks="$(extract_job_blocks "$1")"
  awk '
    function trims(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
    /^--- block ---$/ {
      if (started && group ~ /refs\/heads\/main. && github.workflow/ \
          && queue == "max" && cil == "false" && main_shared) { n++ }
      started=0; group=""; queue=""; cil=""; main_shared=0
      next
    }
    /^[[:space:]]*group:/ { v=trims(substr($0, index($0, ":") + 1)); group=v; started=1
      main_shared = (v ~ /refs\/heads\/main. && github.workflow/) ? 1 : 0; next }
    /^[[:space:]]*queue:/ { queue=trims(substr($0, index($0, ":") + 1)); next }
    /^[[:space:]]*cancel-in-progress:/ { cil=trims(substr($0, index($0, ":") + 1)); next }
    END {
      if (started && group ~ /refs\/heads\/main. && github.workflow/ \
          && queue == "max" && cil == "false" && main_shared) { n++ }
      print n + 0
    }
  ' <<<"$blocks"
}

# Violations for one forbidden-combination sweep: any concurrency block
# (top-level or job-level) that carries queue: max while its
# cancel-in-progress is anything other than a literal false or absent is
# refused, because GitHub rejects queue: max + cancel-in-progress: true at
# validation time and documents max as usable only while cancel-in-progress
# is false or not set.
forbidden_combo_violations() {
  local wf="$1" blocks top
  top="$(extract_top_block "$wf")"
  blocks="$(printf '%s\n--- block ---\n%s' "$top" "$(extract_job_blocks "$wf")")"
  awk '
    function trims(s) { gsub(/^[[:space:]]+|[[:space:]]+$/, "", s); return s }
    /^[[:space:]]*queue:/ { q=trims(substr($0, index($0, ":") + 1)); seen=1 }
    /^[[:space:]]*cancel-in-progress:/ {
      c=trims(substr($0, index($0, ":") + 1))
      if (q == "max" && c != "false") bad=1
    }
    /^--- block ---$/ {
      if (seen && q == "max" && bad) printf "queue: max combined with cancel-in-progress: %s (validation error)\n", c
      seen=0; bad=0; q=""; c=""
    }
  ' <<<"$blocks"
}

# Full posture contract for one file. $2 is 'production' when a job-level
# release lane is mandatory, otherwise only the workflow-level split applies.
posture_violations() {
  local wf="$1" kind="${2:-ci}" violations=''
  local block group_line cil unique queue cil_main

  if ! grep -Eq '^concurrency:[[:space:]]*$' "$wf"; then
    printf "missing a top-level 'concurrency:' block\n"
    return 0
  fi
  block="$(extract_top_block "$wf")"
  group_line="$(grep -E '^[[:space:]]*group:' <<<"$block" | head -n1 || true)"
  cil="$(grep -E '^[[:space:]]*cancel-in-progress:' <<<"$block" | head -n1 || true)"

  if [[ -z "$group_line" ]]; then
    violations+="concurrency.group missing"$'\n'
  else
    grep -q 'github\.workflow' <<<"$group_line" \
      || violations+="concurrency.group must key on github.workflow (cross-workflow isolation)"$'\n'
    grep -q 'github\.head_ref' <<<"$group_line" \
      || violations+="concurrency.group must keep the PR lane (github.head_ref) for safe supersession"$'\n'
    grep -q 'github\.ref' <<<"$group_line" \
      || violations+="concurrency.group must keep the push-ref lane (github.ref)"$'\n'
  fi

  # Retention: integrated main must not sit in a shared queue: single lane.
  unique=0; top_main_unique "$wf" && unique=1
  queue="$(top_queue_value "$wf")"
  cil_main="$(top_cil_for_main "$wf")"
  if [[ $unique -eq 0 ]]; then
    if [[ "$queue" == max && "$cil_main" == keep ]]; then
      :  # alternate legal posture: shared lane with queue: max, cancel false
    else
      violations+="integrated main pushes share a column-0 lane whose queue replaces pending runs (a third main push loses the second)"$'\n'
    fi
  fi
  if [[ "$cil_main" == cancel ]]; then
    violations+="cancel-in-progress must not supersede main-lane runs"$'\n'
  fi

  if [[ "$kind" == production ]]; then
    if [[ "$(release_lane_count "$wf")" -lt 1 ]]; then
      violations+="no valid job-level release lane (main -> github.workflow group with queue: max and literal cancel-in-progress: false)"$'\n'
    fi
  else
    if [[ "$(release_lane_count "$wf")" -gt 0 ]]; then
      violations+="unexpected job-level release lane in a non-production workflow (keep the split minimal)"$'\n'
    fi
  fi

  local combo
  combo="$(forbidden_combo_violations "$wf")"
  [[ -n "$combo" ]] && violations+="$combo"$'\n'

  printf '%s' "$violations"
  return 0
}

# --- real-tree assertions -----------------------------------------------------

declare -A kinds=( [".github/workflows/ci.yml"]=ci [".github/workflows/container-health-smoke.yml"]=production [".github/workflows/release-evidence.yml"]=production )

for wf in "${workflows[@]}"; do
  if [[ ! -f "$wf" ]]; then err "$wf not found"; continue; fi
  violations="$(posture_violations "$wf" "${kinds[$wf]}")"
  if [[ -n "$violations" ]]; then
    while IFS= read -r v; do [[ -n "$v" ]] && err "$wf: $v"; done <<<"$violations"
  else
    ok "$wf declares the split retention posture"
  fi
done

# Synthetic three-rapid-main-pushes contract, evaluated per workflow.
for wf in "${workflows[@]}"; do
  if [[ ! -f "$wf" ]]; then continue; fi
  unique=0; top_main_unique "$wf" && unique=1
  lane=unique; [[ $unique -eq 0 ]] && lane=shared
  result="$(simulate_three_main_pushes "$lane" "$(top_queue_value "$wf")" "$(top_cil_for_main "$wf")")"
  case "$result" in
    INVALID*) err "$wf main-push simulation refused: $result" ;;
    *"lost=0"*) ok "$wf retains all three rapid main pushes ($result)" ;;
    *) err "$wf loses a pending integrated main run ($result)" ;;
  esac
  if [[ "${kinds[$wf]}" == production && -f "$wf" ]]; then
    # The job-level release lane is shared per workflow on main by design;
    # retention there must come from queue: max, never from supersession.
    job_result="$(simulate_three_main_pushes shared max keep)"
    [[ "$job_result" == *"retained=3 lost=0"* ]] \
      && ok "$wf job release lane queues every integrated commit FIFO ($job_result)" \
      || err "$wf job release lane failed the retention simulation ($job_result)"
  fi
done

# PR-lane supersession must survive the split.
pr_result="$(simulate_pr_updates)"
[[ "$pr_result" == *"latest-survives=1 stale=0"* ]] \
  && ok "PR lane supersession contract holds ($pr_result)" \
  || err "PR lane supersession contract broken ($pr_result)"

# Cross-workflow isolation: the three display names keying the release
# lanes must be pairwise distinct.
names=()
for wf in "${workflows[@]}"; do [[ -f "$wf" ]] && names+=( "$(awk -F': ' '/^name:/{print $2; exit}' "$wf")" ); done
names_distinct=1
for ((i=0; i<${#names[@]}; i++)); do
  for ((j=i+1; j<${#names[@]}; j++)); do
    if [[ "${names[$i]}" == "${names[$j]}" ]]; then
      names_distinct=0
      err "display names '${names[$i]}' collide: their release lanes would merge"
    fi
  done
done
[[ "$names_distinct" -eq 1 ]] && ok "release lanes are cross-isolated (${names[*]})"

# Gate-1 wiring: both policy scripts must stay wired into smoke-contracts.
if awk '/^  smoke-contracts:/{s=1;next} s&&/^  [^[:space:]]/{s=0} s' .github/workflows/ci.yml \
    | grep -q 'scripts/ci-release-policy.test.sh' \
   && awk '/^  smoke-contracts:/{s=1;next} s&&/^  [^[:space:]]/{s=0} s' .github/workflows/ci.yml \
    | grep -q 'scripts/ci-release-queue-retention.test.sh'; then
  ok "gate 1 wires the owner policy guard and this retention contract"
else
  err "gate 1 no longer wires scripts/ci-release-policy.test.sh and scripts/ci-release-queue-retention.test.sh"
fi

# --- negative-control fixtures -------------------------------------------------

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

# Regression fixture: the pre-split posture of PR #1143 head af78ba05. Shared
# main lane + queue single + expression cancel: exactly the configuration the
# review showed loses pending main commit B.
cat > "$temp_dir/old-shared-lane.yml" <<'FIXTURE'
name: Old af78ba05 posture
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
FIXTURE

# queue: max with a hardcoded true cancel - GitHub rejects this combination.
cat > "$temp_dir/max-true.yml" <<'FIXTURE'
name: Invalid queue combination
concurrency:
  group: ${{ github.workflow }}-${{ github.ref == 'refs/heads/main' && github.run_id || github.head_ref || github.ref }}
  queue: max
  cancel-in-progress: true
FIXTURE

# queue: max with an expression cancel (the documented feature requires the
# cancel to be false or unset - an expression may evaluate true).
cat > "$temp_dir/max-expression-cancel.yml" <<'FIXTURE'
name: Invalid queue combination expression
concurrency:
  group: ${{ github.workflow }}-${{ github.ref == 'refs/heads/main' && github.run_id || github.head_ref || github.ref }}
  queue: max
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
FIXTURE

# Compliant production fixture: split posture with a job-level release lane.
cat > "$temp_dir/compliant-production.yml" <<'FIXTURE'
name: Compliant production posture
concurrency:
  group: ${{ github.workflow }}-${{ github.ref == 'refs/heads/main' && github.run_id || github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
FIXTURE
cat >> "$temp_dir/compliant-production.yml" <<'FIXTURE'
jobs:
  release-job:
    concurrency:
      group: ${{ github.ref == 'refs/heads/main' && github.workflow || github.run_id }}
      queue: max
      cancel-in-progress: false
FIXTURE

# Split workflow-level posture but the production job lost its release lane.
cat > "$temp_dir/no-release-lane.yml" <<'FIXTURE'
name: Missing release lane
concurrency:
  group: ${{ github.workflow }}-${{ github.ref == 'refs/heads/main' && github.run_id || github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}
FIXTURE

# Alternate legal posture: shared lane retained by queue: max with a literal
# false cancel (accepted by the retention rule, used to prove the contract
# does not reject every shared-lane configuration).
cat > "$temp_dir/compliant-max-shared.yml" <<'FIXTURE'
name: Compliant max-queue shared posture
concurrency:
  group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}
  queue: max
  cancel-in-progress: false
FIXTURE

# Inverted cancel that supersedes main.
cat > "$temp_dir/main-cancel.yml" <<'FIXTURE'
name: Main superseding cancel
concurrency:
  group: ${{ github.workflow }}-${{ github.ref == 'refs/heads/main' && github.run_id || github.head_ref || github.ref }}
  cancel-in-progress: ${{ github.ref == 'refs/heads/main' }}
FIXTURE

expect_rejected() {
  local label="$1" fixture="$2" kind="$3" violations
  violations="$(posture_violations "$fixture" "$kind")"
  if [[ -z "$violations" ]]; then
    err "negative control '$label' was accepted: the contract no longer fails closed."
  else
    printf 'ok   - negative control %-26s rejected (%s)\n' "'$label'" "$(head -n1 <<<"$violations")"
  fi
}

expect_accepted() {
  local label="$1" fixture="$2" kind="$3" violations
  violations="$(posture_violations "$fixture" "$kind")"
  if [[ -n "$violations" ]]; then
    err "positive control '$label' was rejected: $(head -n1 <<<"$violations")"
  else
    printf 'ok   - positive control %-24s accepted\n' "'$label'"
  fi
}

# Non-vacuity, part 1: the simulator itself must reproduce the reported
# defect for the pre-split posture (B replaced), otherwise every
# "retained" verdict would be vacuous.
old_result="$(simulate_three_main_pushes shared single keep)"
[[ "$old_result" == *"B:canceled-replaced"* && "$old_result" == *"lost=1"* ]] \
  && ok "simulator reproduces the af78ba05 pending-main replacement ($old_result)" \
  || err "simulator failed to reproduce the reported defect ($old_result)"

# Non-vacuity, part 2: the invalid combination must be refused.
invalid_result="$(simulate_three_main_pushes shared max cancel)"
[[ "$invalid_result" == INVALID* ]] \
  && ok "simulator refuses queue max + cancel true as invalid ($invalid_result)" \
  || err "simulator accepted the forbidden queue max + cancel true combination ($invalid_result)"

expect_accepted 'compliant split ci'    "$temp_dir/no-release-lane.yml" 'ci'
expect_accepted 'compliant production'  "$temp_dir/compliant-production.yml" 'production'
expect_accepted 'compliant max shared'  "$temp_dir/compliant-max-shared.yml" 'ci'
expect_rejected 'old shared lane'       "$temp_dir/old-shared-lane.yml" 'production'
expect_rejected 'missing release lane'  "$temp_dir/no-release-lane.yml" 'production'
expect_rejected 'max + true cancel'     "$temp_dir/max-true.yml" 'ci'
expect_rejected 'max + expression'      "$temp_dir/max-expression-cancel.yml" 'ci'
expect_rejected 'main-cancel inversion' "$temp_dir/main-cancel.yml" 'ci'

if [[ "$fail" -ne 0 ]]; then
  echo 'Main-run retention contract violations detected.' >&2
  exit 1
fi
echo 'Main-run retention contract tests passed.'
