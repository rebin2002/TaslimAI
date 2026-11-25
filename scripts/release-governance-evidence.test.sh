#!/usr/bin/env bash
set -Eeuo pipefail
readonly repo_root="$(git rev-parse --show-toplevel)"
readonly script="$repo_root/scripts/release-governance-evidence.sh"
readonly temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
commit_sha="$(git -C "$repo_root" rev-parse HEAD)"
GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/governance.json"
bash "$script" "$temp_dir/governance-second.json"
python3 - "$temp_dir/governance.json" "$temp_dir/governance-second.json" "$commit_sha" <<'PY'
import json
from datetime import datetime, timezone
import subprocess
import sys
from pathlib import Path
first = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
second = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
assert first["schemaVersion"] == 1
assert first["source"]["commitSha"] == sys.argv[3]
raw_commit_date = subprocess.check_output(["git", "show", "-s", "--format=%cI", sys.argv[3]], text=True).strip()
expected_commit_date_utc = datetime.fromisoformat(raw_commit_date).astimezone(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
assert first["source"]["commitDateUtc"] == expected_commit_date_utc
assert first["source"]["commitDateUtc"].endswith("Z")
assert first["workflowContract"]["pullRequestCi"] is True
assert first["workflowContract"]["pullRequestReleaseEvidence"] is True
assert first["workflowContract"]["readOnlyContentsPermissions"] is True
assert first["workflowContract"]["allActionsPinnedToCommit"] is True
assert [item["name"] for item in first["workflowContract"]["requiredChecks"]] == [
    "Frontend checks",
    "API integration tests",
    "Browser E2E",
    "Deployment smoke helper contracts",
    "Autopilot next-wave gate",
    "Build production images and smoke-test API",
    "Release evidence and safety contract",
]
assert first["remoteGovernance"]["status"] == "pending_admin_attestation"
assert first["remoteGovernance"]["trackingIssue"] == 283
assert first["releaseAuthorization"] == {
    "integrationPullRequest": 275,
    "status": "human_authorization_required",
    "automaticMergeOrRelease": False,
}
assert first["safetyBoundary"] == {
    "githubSettingsChanged": False,
    "productionChanged": False,
    "deploymentPerformed": False,
    "databaseChanged": False,
    "providerCalled": False,
    "customerCharged": False,
    "secretsRecorded": False,
}
for key in first:
    if key != "generatedAtUtc":
        assert first[key] == second[key], key
serialized = Path(sys.argv[1]).read_text(encoding="utf-8").lower()
for forbidden in ("password", "secretkey", "accesskey", "apikey", "connectionstrings", "github_token"):
    assert forbidden not in serialized, forbidden
PY
if GITHUB_SHA="0000000000000000000000000000000000000000" bash "$script" "$temp_dir/mismatched.json"; then
  echo 'A mismatched GITHUB_SHA must fail closed.' >&2
  exit 1
fi
# An unrelated job title or comment must not satisfy a required check.
fixture="$temp_dir/workflow-fixture"
mkdir -p "$fixture/.github"
cp -R "$repo_root/.github/workflows" "$fixture/.github/"
git -C "$fixture" init --quiet
commit_fixture() {
  git -C "$fixture" add .github/workflows
  git -C "$fixture" -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
}
commit_fixture
sed -i 's/^    name: Build production images and smoke-test API$/    name: Container build # Build production images and smoke-test API/' "$fixture/.github/workflows/container-health-smoke.yml"
# Until the changed workflow is committed, only the recorded commit counts.
(cd "$fixture" && env -u GITHUB_SHA bash "$script" "$temp_dir/unchanged.json") >/dev/null
commit_fixture
if (cd "$fixture" && env -u GITHUB_SHA bash "$script" "$temp_dir/missing-job.json") >"$temp_dir/missing-job.log" 2>&1; then
  echo 'A renamed required container job must fail the evidence check.' >&2
  exit 1
fi
grep -F 'Expected exactly one job named Build production images and smoke-test API' "$temp_dir/missing-job.log" >/dev/null
for mode in wrong-event different-base; do
  case "$mode" in
    wrong-event)
      sed 's/^  pull_request:$/  pull_request_review:/' "$repo_root/.github/workflows/container-health-smoke.yml" > "$fixture/.github/workflows/container-health-smoke.yml"
      ;;
    different-base)
      sed '/^  pull_request:$/a\    branches: [unrelated]' "$repo_root/.github/workflows/container-health-smoke.yml" > "$fixture/.github/workflows/container-health-smoke.yml"
      ;;
  esac
  commit_fixture
  if (cd "$fixture" && env -u GITHUB_SHA bash "$script" "$temp_dir/$mode.json") >"$temp_dir/$mode.log" 2>&1; then
    echo "The evidence check accepted the $mode pull-request fixture." >&2
    exit 1
  fi
  grep -F 'Expected regular pull_request checks for all changes targeting main' "$temp_dir/$mode.log" >/dev/null
done
cp "$repo_root/.github/workflows/container-health-smoke.yml" "$fixture/.github/workflows/"
sed -i 's/^name: Production container smoke$/name: Renamed production smoke/' "$fixture/.github/workflows/container-health-smoke.yml"
commit_fixture
if (cd "$fixture" && env -u GITHUB_SHA bash "$script" "$temp_dir/renamed-workflow.json") >"$temp_dir/renamed-workflow.log" 2>&1; then
  echo 'A renamed workflow must fail the evidence check because its required check context changes.' >&2
  exit 1
fi
grep -F 'Unexpected workflow name in .github/workflows/container-health-smoke.yml' "$temp_dir/renamed-workflow.log" >/dev/null
cp "$repo_root/.github/workflows/container-health-smoke.yml" "$fixture/.github/workflows/"
sed '0,/ # v4/s/ # v4//' "$repo_root/.github/workflows/ci.yml" > "$fixture/.github/workflows/ci.yml"
commit_fixture
if (cd "$fixture" && env -u GITHUB_SHA bash "$script" "$temp_dir/missing-comment.json") >"$temp_dir/missing-comment.log" 2>&1; then
  echo 'Each use of an action needs its own version comment.' >&2
  exit 1
fi
grep -F 'Missing version comment' "$temp_dir/missing-comment.log" >/dev/null
printf '%s\n' 'release-governance-evidence behavior tests passed.'
