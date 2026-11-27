#!/usr/bin/env bash
set -Eeuo pipefail

readonly repo_root="$(git rev-parse --show-toplevel)"
readonly script="$repo_root/scripts/secret-hygiene-audit.sh"
readonly temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

bash "$script" "$repo_root"

fixture_repo="$temp_dir/fixture-repo"
mkdir -p "$fixture_repo"
git -C "$fixture_repo" init -q
git -C "$fixture_repo" config user.email test@example.invalid
git -C "$fixture_repo" config user.name SecretHygieneTest
secret_prefix="sk"
secret_separator="-"
secret_suffix="$(printf 'A%.0s' {1..24})"
printf 'OPENAI_KEY=%s%s%s\n' "$secret_prefix" "$secret_separator" "$secret_suffix" > "$fixture_repo/fixture.env"
git -C "$fixture_repo" add fixture.env

if output="$(bash "$script" "$fixture_repo" 2>&1)"; then
  echo "A high-confidence tracked credential must fail the audit." >&2
  exit 1
fi
if [[ "$output" != *"fixture.env:1 (openai_key)"* ]]; then
  echo "The audit must identify only the fixture path, line, and pattern name." >&2
  printf '%s\n' "$output" >&2
  exit 1
fi
if [[ "$output" == *"$secret_suffix"* ]]; then
  echo "The audit must not print matching credential content." >&2
  exit 1
fi

printf '%s\n' 'Secret hygiene audit tests passed.'
