#!/usr/bin/env bash
# Ref resolution for the bounded next-wave launch gate.
#
# The gate has to run in three supported contexts and must refuse every other
# one. It is a launch gate, so "allowed" means "every next-wave safety invariant
# is still enforced for the revision under review" - never "skip the checks".
#
#   1. push to the expected follow-on branch   -> pre-merge launch review
#   2. push to the expected base branch (main) -> post-merge re-verification
#   3. a pull request that integrates into an allowed base branch
#
# Context 3 is the one that needs care. GitHub checks out a *detached* merge ref
# (refs/pull/<n>/merge) for pull_request workflows, so `git branch --show-current`
# is empty and the only branch information available is GITHUB_HEAD_REF /
# GITHUB_BASE_REF. The gate validates the merge result that will land on the base
# branch, so a pull request is accepted only when its base is an allowed
# integration target *and* the checked-out ref really is that pull request's ref.
# Everything else - including a detached HEAD with no pull request context - is
# refused, so the gate fails closed.

# next_wave_classify_ref <event> <ref> <base_ref> <head_ref> <current_branch> \
#                         <expected_branch> <expected_base>
#
# Prints the resolved context on stdout and returns 0 when the gate may run,
# 1 (with a diagnostic on stderr) otherwise.
next_wave_classify_ref() {
  local event_name="${1:-}"
  local github_ref="${2:-}"
  local base_ref="${3:-}"
  local head_ref="${4:-}"
  local current_branch="${5:-}"
  local expected_branch="${6:-}"
  local expected_base="${7:-}"
  local search_asset_base="fix/search-asset-deep-links-20261005"

  if [[ "$event_name" == "pull_request" ]]; then
    # Billing work and the Search -> Notifications stack are intentionally
    # stacked so each dependent PR can be reviewed and gated independently.
    # Keep those explicit stack targets allowlisted; every other unexpected
    # base still fails closed.
    if [[ "$base_ref" != "$expected_base" && "$base_ref" != "$expected_branch" && "$base_ref" != "$search_asset_base" && "$base_ref" != fix/billing-* ]]; then
      printf 'unexpected pull request base: %s\n' "${base_ref:-<none>}" >&2
      return 1
    fi
    case "$github_ref" in
      refs/pull/*/merge | refs/pull/*/head) ;;
      *)
        printf 'pull request gate must run on a pull request ref: %s\n' "${github_ref:-<none>}" >&2
        return 1
        ;;
    esac
    printf '  event: pull_request\n'
    printf '  base branch: %s\n' "$base_ref"
    printf '  head branch: %s\n' "${head_ref:-<none>}"
    printf '  checked out ref: %s\n' "$github_ref"
    return 0
  fi

  if [[ "$current_branch" != "$expected_branch" && "$current_branch" != "$expected_base" ]]; then
    printf 'unexpected branch: %s\n' "${current_branch:-<none>}" >&2
    return 1
  fi
  printf '  event: %s\n' "${event_name:-local}"
  printf '  branch: %s\n' "$current_branch"
  return 0
}
