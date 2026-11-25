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
"${GOVERNANCE_PYTHON:-python3}" - "$output_path" <<'PY'
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import yaml

output_path = Path(sys.argv[1]).resolve()
repo_root = Path(os.environ["GOVERNANCE_REPO_ROOT"]).resolve()
required_checks = [
    ("Frontend checks", ".github/workflows/ci.yml"),
    ("API integration tests", ".github/workflows/ci.yml"),
    ("Browser E2E", ".github/workflows/ci.yml"),
    ("Deployment smoke helper contracts", ".github/workflows/ci.yml"),
    ("Autopilot next-wave gate", ".github/workflows/ci.yml"),
    ("Build production images and smoke-test API", ".github/workflows/container-health-smoke.yml"),
    ("Release evidence and safety contract", ".github/workflows/release-evidence.yml"),
]
workflow_paths = sorted({relative for _, relative in required_checks})
workflows = {}
pinned_action = re.compile(r"[^\s@]+@[0-9a-fA-F]{40}")
for relative in workflow_paths:
    path = repo_root / relative
    if not path.is_file():
        raise SystemExit(f"Required workflow is missing: {relative}")
    source_text = path.read_text(encoding="utf-8")
    # BaseLoader keeps GitHub's 'on' key and expressions as strings.
    workflow = yaml.load(source_text, Loader=yaml.BaseLoader)
    workflows[relative] = workflow
    triggers = workflow.get("on") or {}
    if "pull_request_target" in triggers or "pull_request" not in triggers:
        raise SystemExit(f"Expected a pull_request trigger without pull_request_target: {relative}")
    if workflow.get("permissions") != {"contents": "read"}:
        raise SystemExit(f"Workflow must declare read-only contents permission: {relative}")
    for job_id, job in workflow["jobs"].items():
        if "permissions" in job and job["permissions"] not in ({}, {"contents": "read"}):
            raise SystemExit(f"Unexpected job permissions: {relative}, {job_id}")
        for entry in [job, *job.get("steps", [])]:
            if "uses" not in entry:
                continue
            action = entry["uses"]
            if not pinned_action.fullmatch(action):
                raise SystemExit(f"Unpinned action: {relative}, {job_id}")
            documented_ref = r"(?m)^\s*(?:-\s+)?uses:\s*" + re.escape(action) + r"[ \t]+#[ \t]*\S"
            if not re.search(documented_ref, source_text):
                raise SystemExit(f"Missing version comment for {action}: {relative}, {job_id}")

release_triggers = workflows[".github/workflows/release-evidence.yml"]["on"]
push_branches = release_triggers.get("push", {}).get("branches", [])
if not {"main", "parallel/**"}.issubset(push_branches):
    raise SystemExit("Release evidence push scope must include main and parallel branches")
for check_name, relative in required_checks:
    matches = [job for job in workflows[relative]["jobs"].values() if job.get("name") == check_name]
    if len(matches) != 1:
        raise SystemExit(f"Expected exactly one job named {check_name} in {relative}")

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
