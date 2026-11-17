#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly script="$repo_root/scripts/release-evidence.sh"
readonly temp_dir="$(mktemp -d)"

# Release-input fixtures the provenance cases below deliberately drift from
# HEAD. The EXIT trap restores them so a failed assertion can never leave the
# CI checkout dirty for the manifest-generation step that runs afterwards.
readonly tampered_input="$repo_root/apps/web/Dockerfile"
readonly migrations_dir="apps/api/Persistence/Migrations"
readonly synthetic_migration="$repo_root/$migrations_dir/99991231000000_SyntheticProvenance.cs"
restore_provenance_state() {
  git -C "$repo_root" checkout -- apps/web/Dockerfile "$migrations_dir" 2>/dev/null || true
  rm -f "$synthetic_migration"
}
trap 'rm -rf "$temp_dir"; restore_provenance_state' EXIT

output="$temp_dir/release-evidence.json"
commit_sha="$(git -C "$repo_root" rev-parse HEAD)"
GITHUB_SHA="$commit_sha" bash "$script" "$output"
bash "$script" "$temp_dir/release-evidence-second.json"

python3 - "$output" "$temp_dir/release-evidence-second.json" "$commit_sha" <<'PY'
import json
import sys
from pathlib import Path

manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
second = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
assert manifest["schemaVersion"] == 1
assert manifest["source"]["commitSha"] == sys.argv[3]
assert len(manifest["source"]["commitSha"]) == 40
assert manifest["buildInputs"]["files"]
assert len(manifest["buildInputs"]["fingerprintSha256"]) == 64
assert manifest["migrations"]["count"] > 0
assert len(manifest["migrations"]["fingerprintSha256"]) == 64
assert manifest["productionSafetyDefaults"]["databaseApplyMigrations"] is True
assert manifest["productionSafetyDefaults"]["customerChargingEnabled"] is False
assert manifest["productionSafetyDefaults"]["openAiEnabled"] is False
assert manifest["productionSafetyDefaults"]["persistentStorageProvider"] == "S3Compatible"
for key in ("source", "buildInputs", "migrations", "baseImages", "productionSafetyDefaults"):
    assert manifest[key] == second[key], key

serialized = Path(sys.argv[1]).read_text(encoding="utf-8").lower()
for forbidden in ("password", "secretkey", "accesskey", "apikey", "connectionstrings"):
    assert forbidden not in serialized, forbidden
PY

# --- Production SHA provenance: the manifest may only describe the commit. ---

# A tracked build input modified after checkout would otherwise be hashed from
# disk and attributed to a commit that never contained that content.
printf '%s\n' '# tampered after checkout' >> "$tampered_input"
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/dirty-input.json" >"$temp_dir/dirty-input.err" 2>&1; then
  echo "A release input that drifts from the recorded commit must fail closed." >&2
  exit 1
fi
grep -q 'drift from the recorded commit' "$temp_dir/dirty-input.err" || {
  echo "The dirty-input refusal must name the provenance drift." >&2
  exit 1
}
restore_provenance_state

# An untracked migration file must not enter the migration fingerprint or
# hijack the chain head reported for rollback decisions.
printf '%s\n' '// synthetic provenance probe; must never reach a manifest' > "$synthetic_migration"
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/untracked-migration.json" >"$temp_dir/untracked-migration.err" 2>&1; then
  echo "An untracked migration file must fail closed against the recorded commit." >&2
  exit 1
fi
grep -q 'SyntheticProvenance' "$temp_dir/untracked-migration.err" || {
  echo "The untracked-migration refusal must name the drifting file." >&2
  exit 1
}
restore_provenance_state

# A deleted tracked migration would silently shrink the fingerprint that stays
# attributed to the recorded commit, so it must fail closed too.
victim="$(git -C "$repo_root" ls-files -- "$migrations_dir" | sed -n '1p')"
if [[ -z "$victim" ]]; then
  echo "Expected tracked migration files to exist for the deletion probe." >&2
  exit 1
fi
rm "$repo_root/$victim"
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/deleted-migration.json" >"$temp_dir/deleted-migration.err" 2>&1; then
  echo "A deleted tracked migration must fail closed against the recorded commit." >&2
  exit 1
fi
grep -q 'drift from the recorded commit' "$temp_dir/deleted-migration.err" || {
  echo "The deletion refusal must name the provenance drift." >&2
  exit 1
}
restore_provenance_state

# No drifted run may leave a manifest behind.
for refused in dirty-input untracked-migration deleted-migration; do
  if [[ -e "$temp_dir/$refused.json" ]]; then
    echo "A refused run must not emit a manifest: $refused" >&2
    exit 1
  fi
done

# Once the checkout matches the recorded commit again, the gate restores
# byte-identical provenance for every commit-attributed section.
GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/restored.json" >/dev/null
python3 - "$output" "$temp_dir/restored.json" <<'PROVENANCE'
import json
import sys
from pathlib import Path

original = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
restored = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
for key in ("source", "buildInputs", "migrations", "baseImages", "productionSafetyDefaults"):
    assert original[key] == restored[key], key
PROVENANCE

if GITHUB_SHA="0000000000000000000000000000000000000000" bash "$script" "$temp_dir/mismatched.json"; then
  echo "A mismatched GITHUB_SHA must fail closed." >&2
  exit 1
fi

printf '%s\n' 'Release evidence behavior tests passed.'
