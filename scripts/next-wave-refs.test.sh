#!/usr/bin/env bash
# Regression tests for the next-wave launch gate ref resolution.
#
# These lock in the fix for the pull-request ref compatibility bug: on a
# pull_request workflow GitHub checks out a detached merge ref, so the gate must
# resolve the context from GITHUB_* instead of a local branch name, and it must
# still refuse every context that is not an allowed integration target.
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/lib/next-wave-refs.sh
. "$ROOT_DIR/scripts/lib/next-wave-refs.sh"

EXPECTED_BRANCH="feature/taslim-autopilot-next-wave"
EXPECTED_BASE="main"

pass=0
fail=0

# check <description> <expected: allow|deny> <event> <ref> <base_ref> <head_ref> <current_branch>
check() {
  local description="$1" expected="$2"
  shift 2
  local output status
  output="$(next_wave_classify_ref "$@" "$EXPECTED_BRANCH" "$EXPECTED_BASE" 2>&1)"
  status=$?
  local actual="deny"
  [[ $status -eq 0 ]] && actual="allow"
  if [[ "$actual" == "$expected" ]]; then
    pass=$((pass + 1))
    printf 'ok   - %s (%s)\n' "$description" "$actual"
  else
    fail=$((fail + 1))
    printf 'FAIL - %s (expected %s, got %s)\n%s\n' "$description" "$expected" "$actual" "$output"
  fi
}

# --- push contexts (unchanged behaviour) -----------------------------------
check "push to the follow-on branch is allowed" \
  allow push refs/heads/$EXPECTED_BRANCH "" "" "$EXPECTED_BRANCH"
check "push to main is allowed" \
  allow push refs/heads/main "" "" main
check "push to an unrelated branch is refused" \
  deny push refs/heads/feature/movie-studio-finalization-wave "" "" feature/movie-studio-finalization-wave
check "push to a parallel branch is refused" \
  deny push refs/heads/parallel/scratch "" "" parallel/scratch
check "a detached local run with no context is refused" \
  deny "" "" "" "" ""

# --- pull request contexts (the bug being fixed) ---------------------------
check "pull request merging into main is allowed" \
  allow pull_request refs/pull/4/merge main feature/movie-studio-finalization-wave ""
check "pull request head ref merging into main is allowed" \
  allow pull_request refs/pull/4/head main feature/movie-studio-finalization-wave ""
check "pull request from the follow-on branch is allowed" \
  allow pull_request refs/pull/9/merge main $EXPECTED_BRANCH ""
check "pull request targeting the follow-on branch is allowed" \
  allow pull_request refs/pull/9/merge $EXPECTED_BRANCH some/other-branch ""
check "pull request into an unrelated base is refused" \
  deny pull_request refs/pull/4/merge develop feature/x ""
check "pull request with no base is refused" \
  deny pull_request refs/pull/4/merge "" feature/x ""
check "pull request event that is not on a pull request ref is refused" \
  deny pull_request refs/heads/main main feature/x ""

printf '\n%s passed, %s failed\n' "$pass" "$fail"
[[ $fail -eq 0 ]]
