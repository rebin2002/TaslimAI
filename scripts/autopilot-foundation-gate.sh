#!/usr/bin/env bash
set -euo pipefail

BASE_SHA="8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a"
EXPECTED_BRANCH="feature/taslim-autopilot-controller"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

printf '%s\n' "[autopilot] verifying base and branch"
test "$(git branch --show-current)" = "$EXPECTED_BRANCH"
git merge-base --is-ancestor "$BASE_SHA" HEAD
printf '  base ancestor: %s\n' "$BASE_SHA"
printf '  branch: %s\n' "$EXPECTED_BRANCH"

printf '%s\n' "[autopilot] repository hygiene"
git diff --check
if git grep -n -E '^(<<<<<<<|=======|>>>>>>>)' -- ':!docs/WAVE*' ':!docs/MOVIE_WAVE*' ':!docs/movie-wave*'; then
  echo "conflict marker found" >&2
  exit 1
fi
if git ls-files | grep -E '(^|/)(\.env|.*\.pem|.*\.p12|.*\.key)$' | grep -v '^\.env\.example$'; then
  echo "tracked secret-like filename found" >&2
  exit 1
fi

printf '%s\n' "[autopilot] safety defaults"
python3 - <<'PY'
import json
from pathlib import Path
for name in ("apps/api/appsettings.json", "apps/api/appsettings.Production.json"):
    data = json.loads(Path(name).read_text())
    assert data["Billing"]["CustomerChargingEnabled"] is False, name
    autopilot = data["Autopilot"]
    assert autopilot["Enabled"] is False, f"{name}: Autopilot.Enabled must default to false"
    assert autopilot["DryRun"] is True, f"{name}: Autopilot.DryRun must default to true"
    assert autopilot["SimulationMode"] is True, f"{name}: Autopilot.SimulationMode must default to true"
    assert autopilot["ChargingEnabled"] is False, f"{name}: Autopilot.ChargingEnabled must be false"
    assert autopilot["PaidProvidersEnabled"] is False, f"{name}: Autopilot.PaidProvidersEnabled must be false"
    assert autopilot["AllowAutomaticIntegrationMerge"] is False, name
    assert autopilot["AllowAutomaticRelease"] is False, name
    assert autopilot["RequireSignedEvents"] is True, name
    assert autopilot["MaxConcurrency"] <= 20, f"{name}: Autopilot.MaxConcurrency must not exceed 20"
print("  autopilot disabled by default and in dry-run/simulation")
print("  customer charging, autopilot charging, and paid providers disabled")
print("  automatic merge and automatic release disabled")
print("  signed events required, concurrency capped at 20")
PY

printf '%s\n' "[autopilot] no destructive database or force-push paths"
if git grep -n -E 'DROP[[:space:]]+DATABASE|ExecuteSqlRaw|ExecuteSqlCommand|--force' -- 'apps/api/Autopilot/*' 'apps/api/Controllers/Autopilot*'; then
  echo "forbidden destructive operation referenced in autopilot code" >&2
  exit 1
fi
python3 - <<'PY'
from pathlib import Path
source = Path("apps/api/Autopilot/AutopilotContracts.cs").read_text()
policy = Path("apps/api/Autopilot/AutopilotPolicies.cs").read_text()
for forbidden in ("force_push", "reset_production_database", "drop_production_data", "disable_charging_guard"):
    assert forbidden in source, f"forbidden action {forbidden} missing from the contract"
assert "CanPerformDestructiveDatabaseAction(string action) => false" in policy, "destructive action guard must always deny"
print("  destructive actions are declared and unconditionally denied")
PY

printf '%s\n' "[autopilot] API build"
dotnet build apps/api/Taslim.Api.csproj --no-restore

printf '%s\n' "[autopilot] deterministic acceptance"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter FullyQualifiedName~Autopilot

if [[ "${RUN_FULL_API:-0}" == "1" ]]; then
  printf '%s\n' "[autopilot] full API regression"
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore
fi

if [[ "${RUN_FRONTEND:-0}" == "1" ]]; then
  printf '%s\n' "[autopilot] frontend checks"
  npm test --workspace @taslim/web
  npm run lint --workspace @taslim/web
  npx tsc --noEmit -p apps/web/tsconfig.json
  NEXT_PUBLIC_API_URL=https://api.example.test npm run build --workspace @taslim/web
fi

if [[ "${RUN_DB_MIGRATIONS:-0}" == "1" ]]; then
  test -n "${FRESH_DATABASE_URL:-}" || { echo "FRESH_DATABASE_URL is required" >&2; exit 1; }
  printf '%s\n' "[autopilot] disposable EF model/migration checks"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$FRESH_DATABASE_URL"
fi

printf '%s\n' "[autopilot] PASS — foundation only; no wave launched, no merge, no deployment, no provider enablement, no charging"
