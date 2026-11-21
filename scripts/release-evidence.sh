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

readonly secret_hygiene_script="$repo_root/scripts/secret-hygiene-audit.sh"
if [[ ! -f "$secret_hygiene_script" ]] || ! git ls-files --error-unmatch -- "scripts/secret-hygiene-audit.sh" >/dev/null 2>&1; then
  echo "Required secret-hygiene audit script is missing or untracked; refusing to emit release evidence." >&2
  exit 1
fi
bash "$secret_hygiene_script" "$repo_root"

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
  "scripts/secret-hygiene-audit.sh"
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

# Production SHA provenance assertion: every fingerprint emitted below is
# attributed to $commit_sha, so the bytes being hashed must be the bytes that
# commit records. A release input that drifts from the recorded commit would
# otherwise produce evidence that no production SHA can reproduce, and a
# migration file that the commit does not contain can even hijack the chain
# head used to judge whether a rollback is database-safe.
#
# `git status` is kept because it names ordinary drift in the porcelain form
# operators already read, and because it is the only cheap check for a
# mode-only change. It is not, on its own, an authority on provenance: it
# hides content edits to a path marked assume-unchanged or skip-worktree, and
# it never reports a file matched by an ignore rule, yet in all three cases the
# bytes still reach the fingerprints below. The assertion therefore compares
# the recorded commit's blobs with the working tree through the object
# database, which consults neither the index state nor the exclude files.
# Any drift from the recorded commit fails closed.
readonly migrations_dir="apps/api/Persistence/Migrations"

provenance_drift=()
while IFS= read -r provenance_line; do
  [[ -n "$provenance_line" ]] || continue
  provenance_drift+=("$provenance_line")
done < <(
  git status --porcelain=v1 --untracked-files=all -- \
    "${required_files[@]}" \
    "$migrations_dir"
)

# Blobs the recorded commit holds for the release inputs. Migration files are
# narrowed to top-level `*.cs`, which is exactly the set hashed below, so an
# unrelated file in that directory cannot fail the gate spuriously.
declare -A provenance_commit_oid=()
while IFS=$'\t' read -r provenance_path provenance_oid; do
  [[ -n "$provenance_path" ]] || continue
  case "$provenance_path" in
    "$migrations_dir/"*)
      provenance_leaf="${provenance_path#"$migrations_dir"/}"
      if [[ "$provenance_leaf" == */* || "$provenance_leaf" != *.cs ]]; then
        continue
      fi
      ;;
  esac
  provenance_commit_oid["$provenance_path"]="$provenance_oid"
done < <(
  git ls-tree -r HEAD -- "${required_files[@]}" "$migrations_dir/" \
    | awk -F'\t' '$1 ~ / blob / { split($1, meta, " "); print $2 "\t" meta[3] }'
)
if [[ "${#provenance_commit_oid[@]}" -eq 0 ]]; then
  echo "The recorded commit $commit_sha lists no release inputs; refusing to emit release evidence." >&2
  exit 1
fi

# Bytes actually present in the checkout, hashed the way the manifest hashes
# them and with the clean/smudge filters bypassed, so the comparison cannot be
# talked out of a match by a filter or by an index flag.
declare -A provenance_disk_oid=()
provenance_present=()
while IFS= read -r provenance_path; do
  [[ -n "$provenance_path" ]] || continue
  if [[ -f "$provenance_path" ]]; then
    provenance_present+=("$provenance_path")
  fi
done < <(
  {
    printf '%s\n' "${required_files[@]}"
    find "$migrations_dir" -maxdepth 1 -type f -name '*.cs'
  } | LC_ALL=C sort -u
)
if [[ "${#provenance_present[@]}" -gt 0 ]]; then
  mapfile -t provenance_present_oid < <(
    printf '%s\n' "${provenance_present[@]}" | git hash-object --no-filters --stdin-paths
  )
  if [[ "${#provenance_present_oid[@]}" -ne "${#provenance_present[@]}" ]]; then
    echo "Release inputs could not all be hashed against the recorded commit; refusing to emit release evidence." >&2
    exit 1
  fi
  for provenance_index in "${!provenance_present[@]}"; do
    provenance_disk_oid["${provenance_present[provenance_index]}"]="${provenance_present_oid[provenance_index]}"
  done
fi

# The commit may not be missing anything the checkout would have hashed, and
# the checkout may not hold anything the commit never recorded.
while IFS= read -r provenance_path; do
  [[ -n "$provenance_path" ]] || continue
  if [[ ! -v provenance_disk_oid["$provenance_path"] ]]; then
    provenance_drift+=("D  $provenance_path (the recorded commit has it, the checkout does not)")
  elif [[ "${provenance_commit_oid["$provenance_path"]}" != "${provenance_disk_oid["$provenance_path"]}" ]]; then
    provenance_drift+=(
      "M  $provenance_path (checkout blob ${provenance_disk_oid["$provenance_path"]} != commit blob ${provenance_commit_oid["$provenance_path"]})"
    )
  fi
done < <(printf '%s\n' "${!provenance_commit_oid[@]}" | LC_ALL=C sort)

while IFS= read -r provenance_path; do
  [[ -n "$provenance_path" ]] || continue
  if [[ ! -v provenance_commit_oid["$provenance_path"] ]]; then
    provenance_drift+=("?? $provenance_path (the checkout has it, the recorded commit does not)")
  fi
done < <(printf '%s\n' "${!provenance_disk_oid[@]}" | LC_ALL=C sort)

if [[ "${#provenance_drift[@]}" -gt 0 ]]; then
  echo "Release inputs drift from the recorded commit $commit_sha; refusing to emit release evidence." >&2
  printf 'Drift: %s\n' "${provenance_drift[@]}" >&2
  exit 1
fi

input_files=(
  "package-lock.json"
  "apps/api/Taslim.Api.csproj"
  "apps/api/Dockerfile"
  "apps/web/Dockerfile"
  "apps/api/appsettings.Production.json"
  "scripts/secret-hygiene-audit.sh"
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
    "secretHygiene": {
        "status": "passed",
        "policy": "high-confidence tracked credential pattern scan",
    },
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
