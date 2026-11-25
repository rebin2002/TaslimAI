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
from fnmatch import fnmatchcase
import json
import os
import re
import subprocess
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
expected_workflow_names = {
    ".github/workflows/ci.yml": "Taslim CI",
    ".github/workflows/container-health-smoke.yml": "Production container smoke",
    ".github/workflows/release-evidence.yml": "Taslim Release Evidence",
}
def event_settings(definition):
    if isinstance(definition, str):
        return {definition: {}}
    if isinstance(definition, list) and all(isinstance(name, str) for name in definition):
        return {name: {} for name in definition}
    return definition if isinstance(definition, dict) else {}


def covers_main_pull_requests(settings):
    if settings in ("", {}):
        return True
    if not isinstance(settings, dict) or set(settings) - {"branches", "branches-ignore", "types"}:
        return False
    default_types = {"opened", "synchronize", "reopened"}
    if "types" in settings and not default_types.issubset(settings["types"]):
        return False
    if "branches" in settings and "branches-ignore" in settings:
        return False
    # GitHub evaluates ordered negations. Ordinary literal, * and ** patterns suffice
    # for main. A pattern with other glob syntax needs a separate verification.
    branches = settings.get("branches", settings.get("branches-ignore", []))
    if not isinstance(branches, list) or not all(
        isinstance(pattern, str) and re.fullmatch(r"!?[a-zA-Z0-9_./*?+-]+", pattern)
        for pattern in branches
    ):
        return False
    matched = False
    for pattern in branches:
        if fnmatchcase("main", pattern.removeprefix("!")):
            matched = not pattern.startswith("!")
    if "branches" in settings:
        return matched
    return not matched


def mapping_value(node, name):
    return next((value for key, value in node.value if key.value == name), None)


def validate_action_references(source_text, relative):
    # Retain the location of each YAML value so that the accompanying comment
    # must be on that occurrence, even when several jobs use the same action.
    document = yaml.compose(source_text, Loader=yaml.BaseLoader)
    jobs = mapping_value(document, "jobs")
    for job_key, job in jobs.value:
        steps = mapping_value(job, "steps")
        for entry in [job, *(steps.value if steps else [])]:
            reference = mapping_value(entry, "uses")
            if reference is None:
                continue
            location = f"{relative}:{reference.start_mark.line + 1} ({job_key.value})"
            if not re.fullmatch(r"[^\s@]+@[0-9a-fA-F]{40}", reference.value):
                raise SystemExit(f"Unpinned action at {location}")
            remainder = source_text[reference.end_mark.index:].partition("\n")[0]
            if not re.fullmatch(r"[ \t]+#[ \t]*\S.*", remainder):
                raise SystemExit(f"Missing version comment at {location}")


workflow_paths = sorted({relative for _, relative in required_checks})
workflows = {}
for relative in workflow_paths:
    source = subprocess.run(
        ["git", "show", f"{os.environ['GOVERNANCE_COMMIT_SHA']}:{relative}"],
        cwd=repo_root, text=True, capture_output=True,
    )
    if source.returncode:
        raise SystemExit(f"Required workflow is missing from the recorded commit: {relative}")
    source_text = source.stdout
    # BaseLoader keeps GitHub's 'on' key and expressions as strings.
    workflow = yaml.load(source_text, Loader=yaml.BaseLoader)
    if workflow.get("name") != expected_workflow_names[relative]:
        raise SystemExit(f"Unexpected workflow name in {relative}: expected {expected_workflow_names[relative]}")
    workflows[relative] = workflow
    triggers = event_settings(workflow.get("on"))
    if "pull_request_target" in triggers or not covers_main_pull_requests(triggers.get("pull_request")):
        raise SystemExit(f"Expected regular pull_request checks for all changes targeting main: {relative}")
    if workflow.get("permissions") != {"contents": "read"}:
        raise SystemExit(f"Workflow must declare read-only contents permission: {relative}")
    for job_id, job in workflow["jobs"].items():
        if "permissions" in job and job["permissions"] not in ({}, {"contents": "read"}):
            raise SystemExit(f"Unexpected job permissions: {relative}, {job_id}")
    validate_action_references(source_text, relative)

release_triggers = workflows[".github/workflows/release-evidence.yml"]["on"]
push_branches = release_triggers.get("push", {}).get("branches", [])
if not {"main", "parallel/**"}.issubset(push_branches):
    raise SystemExit("Release evidence push scope must include main and parallel branches")
for check_name, relative in required_checks:
    matches = [job for job in workflows[relative]["jobs"].values() if job.get("name") == check_name]
    if len(matches) != 1:
        raise SystemExit(f"Expected exactly one job named {check_name} in {relative}")

raw_commit_date = datetime.fromisoformat(os.environ["GOVERNANCE_COMMIT_DATE"])
if raw_commit_date.tzinfo is None:
    raise SystemExit("The recorded commit date has no timezone; refusing to emit governance evidence.")
commit_date_utc = raw_commit_date.astimezone(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

manifest = {
    "schemaVersion": 1,
    "generatedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "source": {
        "commitSha": os.environ["GOVERNANCE_COMMIT_SHA"],
        "commitDateUtc": commit_date_utc,
        "repository": os.environ.get("GOVERNANCE_REPOSITORY", ""),
        "ref": os.environ.get("GOVERNANCE_REF", ""),
    },
    "workflowContract": {
        "workflowFiles": workflow_paths,
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
