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
import sys
from pathlib import Path
first = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
second = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
assert first["schemaVersion"] == 1
assert first["source"]["commitSha"] == sys.argv[3]
assert first["workflowContract"]["pullRequestCi"] is True
assert first["workflowContract"]["pullRequestReleaseEvidence"] is True
assert first["workflowContract"]["readOnlyContentsPermissions"] is True
assert first["workflowContract"]["allActionsPinnedToCommit"] is True
assert [item["name"] for item in first["workflowContract"]["requiredChecks"]] == [
    "Frontend checks",
    "API integration tests",
    "Browser E2E",
    "Deployment smoke helper contracts",
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
printf '%s\n' 'release-governance-evidence behavior tests passed.'
