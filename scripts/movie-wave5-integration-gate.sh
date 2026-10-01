#!/usr/bin/env bash
set -euo pipefail

BASE_SHA="d87ee7fc60c92d4b6a64d37762cdb21418652e07"
EXPECTED_BRANCH="parallel/movie-wave5-integration-prep"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

printf '%s\n' "[wave5] verifying base and branch"
test "$(git branch --show-current)" = "$EXPECTED_BRANCH"
git merge-base --is-ancestor "$BASE_SHA" HEAD
printf '  base ancestor: %s\n' "$BASE_SHA"
printf '  branch: %s\n' "$EXPECTED_BRANCH"

printf '%s\n' "[wave5] repository hygiene"
git diff --check
if git grep -n -E '^(<<<<<<<|=======|>>>>>>>)' -- ':!docs/WAVE*' ':!docs/MOVIE_WAVE*' ':!docs/movie-wave*'; then
  echo "conflict marker found" >&2
  exit 1
fi
if git ls-files | grep -E '(^|/)(\.env|.*\.pem|.*\.p12|.*\.key)$' | grep -v '^\.env\.example$'; then
  echo "tracked secret-like filename found" >&2
  exit 1
fi

printf '%s\n' "[wave5] safety defaults"
python3 - <<'PY'
import json
from pathlib import Path
for name in ("apps/api/appsettings.json", "apps/api/appsettings.Production.json"):
    data = json.loads(Path(name).read_text())
    assert data["Billing"]["CustomerChargingEnabled"] is False, name
    if "MovieVideo" in data:
        assert data["MovieVideo"]["Enabled"] is False, name
    if "DirectVideoProviders" in data:
        assert data["DirectVideoProviders"]["Enabled"] is False, name
    if "VideoGenerationAdapters" in data:
        assert data["VideoGenerationAdapters"]["Enabled"] is False, name
print("  customer charging disabled")
print("  movie/direct video providers disabled")
PY

printf '%s\n' "[wave5] API build"
dotnet build apps/api/Taslim.Api.csproj --no-restore

printf '%s\n' "[wave5] deterministic acceptance"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter FullyQualifiedName~MovieWave5

if [[ "${RUN_FULL_API:-0}" == "1" ]]; then
  printf '%s\n' "[wave5] full API regression"
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore
fi

if [[ "${RUN_FRONTEND:-0}" == "1" ]]; then
  printf '%s\n' "[wave5] frontend checks"
  npm test --workspace @taslim/web
  npx tsc --noEmit -p apps/web/tsconfig.json
  NEXT_PUBLIC_API_URL=https://api.example.test npm run build --workspace @taslim/web
fi

if [[ "${RUN_DB_MIGRATIONS:-0}" == "1" ]]; then
  test -n "${FRESH_DATABASE_URL:-}" || { echo "FRESH_DATABASE_URL is required" >&2; exit 1; }
  test -n "${UPGRADE_DATABASE_URL:-}" || { echo "UPGRADE_DATABASE_URL is required" >&2; exit 1; }
  printf '%s\n' "[wave5] disposable EF model/migration checks"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$FRESH_DATABASE_URL"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$UPGRADE_DATABASE_URL"
fi

if [[ "${RUN_BROWSER_E2E:-0}" == "1" ]]; then
  printf '%s\n' "[wave5] browser E2E is opt-in and requires disposable API/web services"
  npm run test:e2e --workspace @taslim/web
fi

printf '%s\n' "[wave5] PASS — no deployment, provider enablement, or customer charging performed"
