#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly script="$repo_root/scripts/release-evidence.sh"

# Release-input fixtures the provenance cases below deliberately drift from
# HEAD. The EXIT trap restores them so a failed assertion can never leave the
# CI checkout dirty for the manifest-generation step that runs afterwards.
readonly tampered_input="$repo_root/apps/web/Dockerfile"
readonly api_input="$repo_root/apps/api/Dockerfile"
readonly migrations_dir="apps/api/Persistence/Migrations"
readonly synthetic_migration="$repo_root/$migrations_dir/99991231000000_SyntheticProvenance.cs"
# A migration file that an ignore rule hides from `git status` must still be
# refused, so one case plants it behind a rule in the exclude file. The rule is
# a real pattern rather than a commented line, because a line whose first
# character is `#` is a comment in exclude-file syntax and would ignore
# nothing; it is also self-identifying, so the restore can strip exactly it.
readonly exclude_pattern='*_ProvenanceFixture.cs'
readonly hidden_migration="$repo_root/$migrations_dir/99991231000001_ProvenanceFixture.cs"
exclude_path="$(git -C "$repo_root" rev-parse --git-path info/exclude)"
case "$exclude_path" in
  /*) ;;
  *) exclude_path="$repo_root/$exclude_path" ;;
esac
if [[ ! -e "$exclude_path" ]]; then
  echo "The exclude file the ignored-migration fixture needs is not writable: $exclude_path" >&2
  exit 1
fi
# These fixtures rewrite and check out tracked release inputs, clear index
# flags, and temporarily edit info/exclude. Refuse to run if any of that would
# overwrite an operator's existing local work. Perform this BEFORE installing
# the cleanup trap: the trap itself resets the fixture paths to HEAD.
if [[ -n "$(git -C "$repo_root" status --porcelain=v1 --untracked-files=all -- \
  apps/web/Dockerfile apps/api/Dockerfile "$migrations_dir")" ]]; then
  echo "Release-evidence tests require clean Dockerfiles and migrations; local changes were preserved." >&2
  exit 1
fi
if git -C "$repo_root" ls-files -v -- apps/web/Dockerfile apps/api/Dockerfile | grep -Eq '^[a-z]|^S'; then
  echo "Release-evidence tests refuse pre-existing assume-unchanged/skip-worktree flags." >&2
  exit 1
fi
if grep -qxF "$exclude_pattern" "$exclude_path"; then
  echo "Release-evidence tests refuse a pre-existing synthetic exclusion rule." >&2
  exit 1
fi
if [[ -e "$synthetic_migration" || -L "$synthetic_migration" || -e "$hidden_migration" || -L "$hidden_migration" ]]; then
  echo "Release-evidence tests refuse pre-existing synthetic migration fixture paths." >&2
  exit 1
fi

readonly temp_dir="$(mktemp -d)"
restore_provenance_state() {
  # Index flags are cleared before anything is checked out, because while
  # assume-unchanged or skip-worktree is set `git checkout` refuses to put the
  # bytes back, and a leftover flag would poison every later step of the job.
  # Each flag gets its own call: `update-index` takes a single mark flag, and
  # passing the migration directory as a pathspec aborts the whole call with
  # "Unable to mark file" instead of clearing the files that were listed.
  git -C "$repo_root" update-index --no-assume-unchanged -- \
    apps/web/Dockerfile apps/api/Dockerfile >/dev/null 2>&1 || true
  git -C "$repo_root" update-index --no-skip-worktree -- \
    apps/web/Dockerfile apps/api/Dockerfile >/dev/null 2>&1 || true
  if [[ -f "$exclude_path" ]] && grep -qxF "$exclude_pattern" "$exclude_path"; then
    grep -vxF "$exclude_pattern" "$exclude_path" > "$exclude_path.stripped"
    mv "$exclude_path.stripped" "$exclude_path"
  fi
  git -C "$repo_root" checkout -- apps/web/Dockerfile apps/api/Dockerfile "$migrations_dir" 2>/dev/null || true
  rm -f "$synthetic_migration" "$hidden_migration"
  # A restore that silently failed would leave the tampered bytes on disk for
  # the next assertion to mistake for the commit's own content, and would let
  # the manifest-generation step of the same job inherit a dirty checkout.
  # Refuse loudly instead; the gate itself also fails closed on drift, so a
  # broken restore can never turn into a false green.
  local provenance_path
  for provenance_path in apps/web/Dockerfile apps/api/Dockerfile; do
    if [[ "$(git -C "$repo_root" hash-object --no-filters -- "$provenance_path")" \
      != "$(git -C "$repo_root" rev-parse -q --verify "HEAD:$provenance_path")" ]]; then
      echo "release-evidence fixture restore left $provenance_path drifted from HEAD." >&2
      exit 1
    fi
  done
  if [[ -n "$(git -C "$repo_root" ls-files -v -- apps/web/Dockerfile apps/api/Dockerfile | grep -E '^[a-z]|^S' )" ]]; then
    echo "release-evidence fixture restore left an index flag set." >&2
    exit 1
  fi
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

# An ignore rule must not hide a release input from the assertion. `find` still
# enumerates the file, and its bytes are still what the manifest would hash, so
# an excluded migration has to fail closed even though `git status` is silent.
printf '%s\n' "$exclude_pattern" >> "$exclude_path"
printf '%s\n' '// ignored synthetic provenance probe' > "$hidden_migration"
if ! git -C "$repo_root" check-ignore -q -- "$migrations_dir/99991231000001_ProvenanceFixture.cs"; then
  echo "The ignored-migration fixture must actually be excluded by the ignore rule." >&2
  exit 1
fi
if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=all -- "$migrations_dir")" ]]; then
  echo "The ignored-migration fixture must be invisible to git status to prove the assertion." >&2
  exit 1
fi
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/ignored-migration.json" >"$temp_dir/ignored-migration.err" 2>&1; then
  echo "A migration file hidden by an ignore rule must still fail closed." >&2
  exit 1
fi
grep -q 'ProvenanceFixture' "$temp_dir/ignored-migration.err" || {
  echo "The ignored-migration refusal must name the drifting file." >&2
  exit 1
}
restore_provenance_state

# assume-unchanged suppresses a content edit from `git status` while leaving
# the tampered bytes on disk for the manifest to hash and attribute to a commit
# that never held them.
git -C "$repo_root" update-index --assume-unchanged -- apps/web/Dockerfile
printf '%s\n' '# tampered under assume-unchanged' >> "$tampered_input"
if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=all -- apps/web/Dockerfile)" ]]; then
  echo "The assume-unchanged fixture must be invisible to git status to prove the assertion." >&2
  exit 1
fi
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/assume-unchanged.json" >"$temp_dir/assume-unchanged.err" 2>&1; then
  echo "A release input tampered under assume-unchanged must fail closed." >&2
  exit 1
fi
grep -q 'apps/web/Dockerfile' "$temp_dir/assume-unchanged.err" || {
  echo "The assume-unchanged refusal must name the drifting input." >&2
  exit 1
}
restore_provenance_state

# skip-worktree hides the same class of drift through a different index flag,
# on a second required input, so the assertion is not proven per-path.
git -C "$repo_root" update-index --skip-worktree -- apps/api/Dockerfile
printf '%s\n' '# tampered under skip-worktree' >> "$api_input"
if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=all -- apps/api/Dockerfile)" ]]; then
  echo "The skip-worktree fixture must be invisible to git status to prove the assertion." >&2
  exit 1
fi
if GITHUB_SHA="$commit_sha" bash "$script" "$temp_dir/skip-worktree.json" >"$temp_dir/skip-worktree.err" 2>&1; then
  echo "A release input tampered under skip-worktree must fail closed." >&2
  exit 1
fi
grep -q 'apps/api/Dockerfile' "$temp_dir/skip-worktree.err" || {
  echo "The skip-worktree refusal must name the drifting input." >&2
  exit 1
}
restore_provenance_state

# No drifted run may leave a manifest behind.
for refused in dirty-input untracked-migration deleted-migration ignored-migration assume-unchanged skip-worktree; do
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
