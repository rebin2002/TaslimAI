#!/usr/bin/env bash
# Emit secret-free acceptance evidence for release governance.
#
# This script audits repository-controlled workflow contracts only. It does not
# call GitHub APIs, change branch protection, alter repository settings, merge,
# deploy, or authorize PR #275. Remote main protection and ruleset state remain
# an administrator-attested acceptance item tracked by issue #283.
set -Eeuo pipefail
readonly repo_root="$(git rev-parse --show-toplevel)"
readonly output_path="${1:-${GOVERNANCE_EVIDENCE_PATH:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/taslim-release-governance-evidence.json}}"
if [[ "$#" -gt 1 ]]; then
  echo "Usage: $0 [output-path]" >&2
  exit 2
fi
cd "$repo_root"
readonly commit_sha="$(git rev-parse HEAD)"
if ! [[ "$commit_sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo "The checked-out revision is not a full commit SHA; refusing to emit governance evidence." >&2
  exit 1
fi
if [[ -n "${GITHUB_SHA:-}" && "${GITHUB_SHA,,}" != "$commit_sha" ]]; then
  echo "GITHUB_SHA does not match the checked-out revision; refusing to emit governance evidence." >&2
  exit 1
fi
export GOVERNANCE_REPO_ROOT="$repo_root"
export GOVERNANCE_COMMIT_SHA="$commit_sha"
export GOVERNANCE_COMMIT_DATE="$(git show -s --format=%cI HEAD)"
export GOVERNANCE_REPOSITORY="${GITHUB_REPOSITORY:-}"
export GOVERNANCE_REF="${GITHUB_REF:-}"
export GOVERNANCE_RUN_ID="${GITHUB_RUN_ID:-}"
python3 - "$output_path" <<'PY'
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

output_path = Path(sys.argv[1]).resolve()
repo_root = Path(os.environ["GOVERNANCE_REPO_ROOT"]).resolve()
workflow_paths = [repo_root / ".github/workflows/ci.yml", repo_root / ".github/workflows/release-evidence.yml"]
required_checks = [
    ("Frontend checks", ".github/workflows/ci.yml"),
    ("API integration tests", ".github/workflows/ci.yml"),
    ("Browser E2E", ".github/workflows/ci.yml"),
    ("Deployment smoke helper contracts", ".github/workflows/ci.yml"),
    ("Release evidence and safety contract", ".github/workflows/release-evidence.yml"),
]
for workflow in workflow_paths:
    if not workflow.is_file():
        raise SystemExit(f"Required workflow is missing: {workflow.relative_to(repo_root)}")

workflow_text = {str(path.relative_to(repo_root)): path.read_text(encoding="utf-8") for path in workflow_paths}
for relative, text in workflow_text.items():
    if "pull_request_target" in text:
        raise SystemExit(f"Unsafe pull_request_target trigger found in {relative}")
    if not re.search(r"(?m)^permissions:\s*\n\s+contents:\s+read\s*$", text):
        raise SystemExit(f"Workflow must declare read-only contents permission: {relative}")
    for line_number, line in enumerate(text.splitlines(), 1):
        if "uses:" not in line:
            continue
        match = re.search(r"uses:\s*([^\s#]+)@([0-9a-fA-F]{40})\s+#\s*\S.*$", line.strip())
        if match is None:
            raise SystemExit(f"Unpinned or undocumented action at {relative}:{line_number}")

ci = workflow_text[".github/workflows/ci.yml"]
release = workflow_text[".github/workflows/release-evidence.yml"]
if not re.search(r"(?m)^\s*pull_request:\s*$", ci):
    raise SystemExit("CI must run for pull requests")
if not re.search(r"(?m)^\s*pull_request:\s*$", release):
    raise SystemExit("Release evidence must run for pull requests")
if "branches: [main, \"parallel/**\"]" not in release:
    raise SystemExit("Release evidence push scope must include main and parallel branches")
for check_name, relative in required_checks:
    if check_name not in workflow_text[relative]:
        raise SystemExit(f"Required check context is missing: {check_name}")

manifest = {
    "schemaVersion": 1,
    "generatedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "source": {
        "commitSha": os.environ["GOVERNANCE_COMMIT_SHA"],
        "commitDateUtc": os.environ["GOVERNANCE_COMMIT_DATE"],
        "repository": os.environ.get("GOVERNANCE_REPOSITORY", ""),
        "ref": os.environ.get("GOVERNANCE_REF", ""),
    },
    "workflowContract": {
        "pullRequestCi": True,
        "pullRequestReleaseEvidence": True,
        "readOnlyContentsPermissions": True,
        "allActionsPinnedToCommit": True,
        "requiredChecks": [{"name": name, "workflow": workflow} for name, workflow in required_checks],
    },
    "remoteGovernance": {
        "status": "pending_admin_attestation",
        "mainProtection": "not_verified_by_local_evidence",
        "ruleset": "not_verified_by_local_evidence",
        "trackingIssue": 283,
        "requiredAcceptance": [
            "protected main ref",
            "required CI and release-evidence checks",
            "pull-request review and resolved conversations",
            "current-head status checks",
            "no direct push, force push, or branch deletion",
            "documented least-privileged bypass actors",
            "disposable failed-check merge rejection test",
        ],
    },
    "releaseAuthorization": {
        "integrationPullRequest": 275,
        "status": "human_authorization_required",
        "automaticMergeOrRelease": False,
    },
    "safetyBoundary": {
        "githubSettingsChanged": False,
        "productionChanged": False,
        "deploymentPerformed": False,
        "databaseChanged": False,
        "providerCalled": False,
        "customerCharged": False,
        "secretsRecorded": False,
    },
}
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(f"Release governance evidence written to {output_path}")
print(f"Source commit: {manifest['source']['commitSha']}")
print("Remote main protection: pending administrator attestation (issue #283)")
print("PR #275 release authorization: human decision required")
PY
manifest_sha256="$(sha256sum "$output_path" | awk '{print $1}')"
echo "Release governance evidence SHA-256: $manifest_sha256"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo '## Release governance evidence'
    echo
    echo "- Source commit: \`$commit_sha\`"
    echo "- Manifest SHA-256: \`$manifest_sha256\`"
    echo '- Local workflow contract: **verified**'
    echo '- Main protection/ruleset: **pending administrator attestation; see issue #283**'
    echo '- PR #275 integration/release authorization: **human decision required**'
    echo '- No repository settings, production systems, deployments, providers, databases, or billing were changed.'
  } >> "$GITHUB_STEP_SUMMARY"
fi
