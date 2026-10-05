#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly script="$repo_root/scripts/release-evidence.sh"
readonly temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

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

if GITHUB_SHA="0000000000000000000000000000000000000000" bash "$script" "$temp_dir/mismatched.json"; then
  echo "A mismatched GITHUB_SHA must fail closed." >&2
  exit 1
fi

printf '%s\n' 'Release evidence behavior tests passed.'
