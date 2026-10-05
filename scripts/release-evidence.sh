#!/usr/bin/env bash
# Emit a secret-free, stable release identity manifest for rollback diagnosis.
#
# This gate does not deploy, contact production, apply migrations, or activate
# providers. It records the exact checked-out revision and hashes the inputs
# that operators need to compare two candidate builds safely.
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly output_path="${1:-${RELEASE_EVIDENCE_PATH:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/taslim-release-evidence.json}}"

if [[ "$#" -gt 1 ]]; then
  echo "Usage: $0 [output-path]" >&2
  exit 2
fi

cd "$repo_root"

readonly commit_sha="$(git rev-parse HEAD)"
if ! [[ "$commit_sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo "The checked-out revision is not a full commit SHA; refusing to emit release evidence." >&2
  exit 1
fi

if [[ -n "${GITHUB_SHA:-}" && "${GITHUB_SHA,,}" != "$commit_sha" ]]; then
  echo "GITHUB_SHA does not match the checked-out revision; refusing to emit release evidence." >&2
  echo "Expected: ${GITHUB_SHA,,}" >&2
  echo "Actual:   $commit_sha" >&2
  exit 1
fi

required_files=(
  "package-lock.json"
  "apps/api/Taslim.Api.csproj"
  "apps/api/Dockerfile"
  "apps/web/Dockerfile"
  "apps/api/appsettings.Production.json"
)
for path in "${required_files[@]}"; do
  if [[ ! -f "$path" ]]; then
    echo "Required release input is missing: $path" >&2
    exit 1
  fi
  if ! git ls-files --error-unmatch -- "$path" >/dev/null 2>&1; then
    echo "Required release input is not tracked: $path" >&2
    exit 1
  fi
done

mapfile -t migration_files < <(
  find apps/api/Persistence/Migrations -maxdepth 1 -type f -name '*.cs' -printf '%f\n' | sort
)
if [[ "${#migration_files[@]}" -eq 0 ]]; then
  echo "No checked-in EF migration files were found; refusing to emit release evidence." >&2
  exit 1
fi

migration_latest="$(printf '%s\n' "${migration_files[@]}" | grep -E '^[0-9]{14}_.*\.cs$' | grep -v '\.Designer\.cs$' | tail -n 1)"
if [[ -z "$migration_latest" ]]; then
  echo "No primary EF migration was found; refusing to emit release evidence." >&2
  exit 1
fi

input_files=(
  "package-lock.json"
  "apps/api/Taslim.Api.csproj"
  "apps/api/Dockerfile"
  "apps/web/Dockerfile"
  "apps/api/appsettings.Production.json"
)

export RELEASE_EVIDENCE_REPO_ROOT="$repo_root"
export RELEASE_EVIDENCE_COMMIT_SHA="$commit_sha"
export RELEASE_EVIDENCE_COMMIT_DATE="$(git show -s --format=%cI HEAD)"
export RELEASE_EVIDENCE_REPOSITORY="${GITHUB_REPOSITORY:-}"
export RELEASE_EVIDENCE_REF="${GITHUB_REF:-}"
export RELEASE_EVIDENCE_RUN_ID="${GITHUB_RUN_ID:-}"
export RELEASE_EVIDENCE_RUN_ATTEMPT="${GITHUB_RUN_ATTEMPT:-}"
export RELEASE_EVIDENCE_NODE_VERSION="${NODE_VERSION:-not-specified}"
export RELEASE_EVIDENCE_DOTNET_VERSION="${DOTNET_VERSION:-not-specified}"
export RELEASE_EVIDENCE_MIGRATION_LATEST="$migration_latest"
export RELEASE_EVIDENCE_MIGRATION_FILES="$(printf '%s\n' "${migration_files[@]}")"
export RELEASE_EVIDENCE_INPUT_FILES="$(printf '%s\n' "${input_files[@]}")"

python3 - "$output_path" <<'PY'
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

output_path = Path(sys.argv[1]).resolve()
repo_root = Path(os.environ["RELEASE_EVIDENCE_REPO_ROOT"]).resolve()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def env_list(name: str) -> list[str]:
    return [item for item in os.environ.get(name, "").splitlines() if item]


def nested(mapping: dict, *keys: str):
    value = mapping
    for key in keys:
        if not isinstance(value, dict) or key not in value:
            return None
        value = value[key]
    return value

production_path = repo_root / "apps/api/appsettings.Production.json"
with production_path.open(encoding="utf-8") as handle:
    production = json.load(handle)

safety_defaults = {
    "databaseApplyMigrations": nested(production, "Database", "ApplyMigrations") is True,
    "customerChargingEnabled": nested(production, "Billing", "CustomerChargingEnabled") is True,
    "openAiEnabled": nested(production, "Ai", "OpenAI", "Enabled") is True,
    "persistentStorageProvider": nested(production, "Files", "StorageProvider"),
}
if not safety_defaults["databaseApplyMigrations"]:
    raise SystemExit("Production Database:ApplyMigrations must remain true in the release contract.")
if safety_defaults["customerChargingEnabled"]:
    raise SystemExit("Production customer charging must remain disabled in the release contract.")
if safety_defaults["openAiEnabled"]:
    raise SystemExit("Production OpenAI activation must remain disabled in the release contract.")
if safety_defaults["persistentStorageProvider"] != "S3Compatible":
    raise SystemExit("Production storage must remain S3Compatible in the release contract.")

input_entries = []
for relative in env_list("RELEASE_EVIDENCE_INPUT_FILES"):
    path = repo_root / relative
    input_entries.append({"path": relative, "sha256": sha256_file(path)})
input_fingerprint = hashlib.sha256(
    "".join(f"{entry['path']}\0{entry['sha256']}\n" for entry in input_entries).encode()
).hexdigest()

migration_entries = []
for relative in env_list("RELEASE_EVIDENCE_MIGRATION_FILES"):
    path = repo_root / "apps/api/Persistence/Migrations" / relative
    migration_entries.append({"path": f"apps/api/Persistence/Migrations/{relative}", "sha256": sha256_file(path)})
migration_fingerprint = hashlib.sha256(
    "".join(f"{entry['path']}\0{entry['sha256']}\n" for entry in migration_entries).encode()
).hexdigest()

base_images = []
for dockerfile in (repo_root / "apps/api/Dockerfile", repo_root / "apps/web/Dockerfile"):
    for line in dockerfile.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^FROM\s+(\S+)", line)
        if match:
            base_images.append({"dockerfile": str(dockerfile.relative_to(repo_root)), "image": match.group(1)})

commit_date = os.environ["RELEASE_EVIDENCE_COMMIT_DATE"]
manifest = {
    "schemaVersion": 1,
    "generatedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "source": {
        "commitSha": os.environ["RELEASE_EVIDENCE_COMMIT_SHA"],
        "commitDateUtc": commit_date,
        "repository": os.environ.get("RELEASE_EVIDENCE_REPOSITORY", ""),
        "ref": os.environ.get("RELEASE_EVIDENCE_REF", ""),
    },
    "workflow": {
        "runId": os.environ.get("RELEASE_EVIDENCE_RUN_ID", ""),
        "runAttempt": os.environ.get("RELEASE_EVIDENCE_RUN_ATTEMPT", ""),
    },
    "buildInputs": {
        "nodeVersion": os.environ["RELEASE_EVIDENCE_NODE_VERSION"],
        "dotnetVersion": os.environ["RELEASE_EVIDENCE_DOTNET_VERSION"],
        "files": input_entries,
        "fingerprintSha256": input_fingerprint,
    },
    "migrations": {
        "count": sum(1 for entry in migration_entries if re.match(r".*/[0-9]{14}_.+\.cs$", entry["path"]) and not entry["path"].endswith(".Designer.cs")),
        "latestFile": f"apps/api/Persistence/Migrations/{os.environ['RELEASE_EVIDENCE_MIGRATION_LATEST']}",
        "files": migration_entries,
        "fingerprintSha256": migration_fingerprint,
    },
    "baseImages": base_images,
    "productionSafetyDefaults": safety_defaults,
}

output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(f"Release evidence written to {output_path}")
print(f"Source commit: {manifest['source']['commitSha']}")
print(f"Build-input fingerprint: {input_fingerprint}")
print(f"Migration fingerprint: {migration_fingerprint}")
PY

manifest_sha256="$(sha256sum "$output_path" | awk '{print $1}')"
echo "Release evidence manifest SHA-256: $manifest_sha256"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo '## Release evidence'
    echo
    echo "- Source commit: \`${commit_sha}\`"
    echo "- Manifest SHA-256: \`${manifest_sha256}\`"
    echo "- Migration chain head: \`${migration_latest}\`"
    echo '- Production charging default: **disabled**'
    echo '- Production paid-provider activation default: **disabled**'
    echo '- No deployment, migration application, or provider call was performed.'
  } >> "$GITHUB_STEP_SUMMARY"
fi
