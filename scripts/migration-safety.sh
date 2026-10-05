#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly api_project="apps/api"
readonly migration_script_path="${MIGRATION_SCRIPT_PATH:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/taslim-migrations.sql}"

if [[ -z "${MIGRATION_DATABASE_URL:-}" ]]; then
  echo "MIGRATION_DATABASE_URL must be set to a disposable PostgreSQL database." >&2
  exit 2
fi

cd "$repo_root"
mkdir -p "$(dirname "$migration_script_path")"
rm -f "$migration_script_path"

run_ef() {
  dotnet ef "$@" \
    --project "$api_project" \
    --startup-project "$api_project" \
    --configuration Release \
    --no-build
}

echo "Checking that the EF model is represented by a checked-in migration..."
run_ef migrations has-pending-model-changes

echo "Applying the complete migration chain to a disposable PostgreSQL database..."
run_ef database update --connection "$MIGRATION_DATABASE_URL"

echo "Replaying the migration command to verify an already-current database is safe..."
run_ef database update --connection "$MIGRATION_DATABASE_URL"

echo "Generating the idempotent migration script for release inspection..."
run_ef migrations script --idempotent --output "$migration_script_path"

if [[ ! -s "$migration_script_path" ]]; then
  echo "EF generated an empty migration script; refusing to pass the migration gate." >&2
  exit 1
fi

if ! grep -q '__EFMigrationsHistory' "$migration_script_path"; then
  echo "The generated script does not contain EF migration history tracking; refusing to pass." >&2
  exit 1
fi

readonly script_sha256="$(sha256sum "$migration_script_path" | awk '{print $1}')"
readonly script_bytes="$(wc -c < "$migration_script_path" | tr -d '[:space:]')"
readonly script_lines="$(wc -l < "$migration_script_path" | tr -d '[:space:]')"

echo "Migration safety gate passed."
echo "Idempotent script: $migration_script_path"
echo "Idempotent script SHA-256: $script_sha256"
echo "Idempotent script size: ${script_bytes} bytes (${script_lines} lines)"

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo '## Migration safety'
    echo
    echo '- EF model drift: **none detected**'
    echo '- Fresh database migration chain: **applied**'
    echo '- Repeat migration run: **no-op**'
    echo "- Idempotent SQL artifact: **${script_bytes} bytes / ${script_lines} lines**"
    echo "- Idempotent SQL SHA-256: \`$script_sha256\`"
  } >> "$GITHUB_STEP_SUMMARY"
fi
