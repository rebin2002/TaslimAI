#!/usr/bin/env bash
set -euo pipefail

BASE_SHA="5d4c53ce73fb3d02561c122fa1ed68f70cd9b98f"
EXPECTED_BRANCH="feature/taslim-autopilot-next-wave"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

printf '%s\n' "[next-wave] verifying base and branch"
CURRENT_BRANCH="$(git branch --show-current)"
if [[ -z "$CURRENT_BRANCH" ]]; then CURRENT_BRANCH="${GITHUB_HEAD_REF:-${GITHUB_REF_NAME:-}}"; fi
test "$CURRENT_BRANCH" = "$EXPECTED_BRANCH"
git merge-base --is-ancestor "$BASE_SHA" HEAD
printf '  base ancestor: %s\n' "$BASE_SHA"
printf '  branch: %s\n' "$CURRENT_BRANCH"

printf '%s\n' "[next-wave] repository hygiene"
git diff --check
if git grep -n -E '^(<<<<<<<|=======|>>>>>>>)' -- ':!docs/WAVE*' ':!docs/MOVIE_WAVE*' ':!docs/movie-wave*'; then
  echo "conflict marker found" >&2
  exit 1
fi
if git ls-files | grep -E '(^|/)(\.env|.*\.pem|.*\.p12|.*\.key)$' | grep -v '^\.env\.example$'; then
  echo "tracked secret-like filename found" >&2
  exit 1
fi

printf '%s\n' "[next-wave] safety defaults and bounds"
python3 - <<'PY'
import json
from pathlib import Path
for name in ("apps/api/appsettings.json", "apps/api/appsettings.Production.json"):
    data = json.loads(Path(name).read_text())
    assert data["Billing"]["CustomerChargingEnabled"] is False, name
    autopilot = data["Autopilot"]
    assert autopilot["Enabled"] is False, f"{name}: Autopilot.Enabled must default to false"
    assert autopilot["DryRun"] is True, f"{name}: Autopilot.DryRun must default to true"
    assert autopilot["SimulationMode"] is True, name
    assert autopilot["ChargingEnabled"] is False, name
    assert autopilot["PaidProvidersEnabled"] is False, name
    assert autopilot["AllowAutomaticIntegrationMerge"] is False, name
    assert autopilot["AllowAutomaticRelease"] is False, name
    assert autopilot["RequireSignedEvents"] is True, name
    assert autopilot["MaxConcurrency"] <= 20, name
    assert autopilot["AllowNextWaveLaunch"] is False, f"{name}: follow-on waves stay off until requested"
    assert autopilot["AllowNextWaveLaunchWhenReleasePending"] is False, name
    assert autopilot["MaxTasksPerWave"] <= 20, f"{name}: MaxTasksPerWave must not exceed 20"
    assert autopilot["MaxLaunchAttempts"] <= 3, f"{name}: MaxLaunchAttempts must not exceed 3"
    assert autopilot["PollingFallbackAfterMinutes"] >= 5, name
print("  controller disabled, dry-run, follow-on launch off by default")
print("  charging, paid providers, automatic merge, and automatic release disabled")
print("  wave task bound 20 and launch attempt bound 3 enforced in shipped defaults")
PY

printf '%s\n' "[next-wave] no destructive, force-push, or release paths"
if git grep -n -E 'DROP[[:space:]]+DATABASE|ExecuteSqlRaw|ExecuteSqlCommand|--force' -- 'apps/api/Autopilot/*' 'apps/api/Controllers/Autopilot*'; then
  echo "forbidden destructive operation referenced in autopilot code" >&2
  exit 1
fi
python3 - <<'PY'
from pathlib import Path
source = Path("apps/api/Autopilot/AutopilotContracts.cs").read_text()
policy = Path("apps/api/Autopilot/AutopilotPolicies.cs").read_text()
nextwave = Path("apps/api/Autopilot/AutopilotNextWavePolicy.cs").read_text()
for forbidden in ("force_push", "reset_production_database", "drop_production_data", "disable_charging_guard"):
    assert forbidden in source, f"forbidden action {forbidden} missing from the contract"
assert "CanPerformDestructiveDatabaseAction(string action) => false" in policy, "destructive action guard must always deny"
# The follow-on loop may only perform ordinary development launches.
assert '!string.Equals(action, "production_release"' in nextwave, "next-wave policy must refuse production release"
assert '!string.Equals(action, "integration_merge"' in nextwave, "next-wave policy must refuse integration merge"
assert "IsForbiddenAction(action)" in nextwave, "next-wave policy must inherit the forbidden-action guard"
options_source = Path("apps/api/Autopilot/AutopilotOptions.cs").read_text()
assert "MaxTasksPerWave = Math.Clamp(MaxTasksPerWave, 1, 20)" in options_source, "wave task bound must be clamped to 20"
assert "MaxLaunchAttempts = Math.Clamp(MaxLaunchAttempts, 0, 3)" in options_source, "launch attempt bound must be clamped to 3"
print("  destructive actions denied; follow-on loop limited to development launches")
PY

printf '%s\n' "[next-wave] provider-neutral boundary and credential isolation"
python3 - <<'PY'
from pathlib import Path
provider = Path("apps/api/Autopilot/ManusBridgeWaveLaunchProvider.cs").read_text()
service = Path("apps/api/Autopilot/AutopilotNextWaveService.cs").read_text()
options = Path("apps/api/Autopilot/AutopilotOptions.cs").read_text()
# The bridge token is read only from the environment, never from configuration values.
assert "Environment.GetEnvironmentVariable" in provider, "bridge credential must come from the environment"
assert "Configuration" not in provider, "the provider must not read configuration values directly"
assert "Bearer" in provider, "the credential must be sent as a bearer header"
# No launch state or controller source may capture credential material.
for name in ("AutopilotWaveLaunchEntities.cs", "AutopilotNextWaveService.cs"):
    text = Path("apps/api/Autopilot/" + name).read_text()
    assert "BridgeToken" not in text, f"{name} must not reference the bridge credential"
    assert "Environment.GetEnvironmentVariable" not in text, f"{name} must not read credentials"
    assert "Authorization" not in text, f"{name} must not build credential headers"
assert "BridgeToken" not in Path("apps/api/Autopilot/AutopilotBacklogService.cs").read_text()
assert "BridgeToken" not in Path("apps/api/Autopilot/AutopilotContracts.cs").read_text(), "the credential name must not leak into API contracts"
assert "BridgeTokenEnvironmentVariable" in options
# Live launch requires an explicit opt-in flag and stays off by default.
assert "public bool AllowNextWaveLaunch { get; set; } = false;" in options, "live follow-on launch must default to off"
assert "LiveNextWaveLaunchEnabled => AllowNextWaveLaunch && !DryRun" in options, "live launch requires both the opt-in and non-dry-run"
assert "MaxTasksPerWave" in options and "MaxLaunchAttempts" in options
assert "public int MaxTasksPerWave { get; set; } = 20;" in options, "wave task bound default must be 20"
assert "public int MaxLaunchAttempts { get; set; } = 3;" in options, "launch attempt bound default must be 3"
assert "IWaveLaunchProvider" in service, "the service must depend on the provider-neutral interface"
print("  credential read from the environment only and never persisted")
print("  launch goes through the provider-neutral interface with bounded attempts")
PY

printf '%s\n' "[next-wave] additive migration present"
test -f apps/api/Persistence/Migrations/20261002134838_AddAutopilotNextWaveLaunchAndBacklog.cs
python3 - <<'PY'
from pathlib import Path
migration = Path("apps/api/Persistence/Migrations/20261002134838_AddAutopilotNextWaveLaunchAndBacklog.cs").read_text()
for table in ("AutopilotWaveLaunchBatches", "AutopilotWaveLaunchTasks", "AutopilotBacklogItems"):
    assert table in migration, f"{table} missing from the additive migration"
assert "DropColumn" not in migration and "DropTable" not in migration.split("Down(")[0], "migration must be additive in Up()"
print("  adds the three follow-on tables only; no destructive Up() operations")
PY

printf '%s\n' "[next-wave] API build"
dotnet build apps/api/Taslim.Api.csproj --no-restore

printf '%s\n' "[next-wave] deterministic acceptance (foundation + follow-on)"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter FullyQualifiedName~Autopilot

if [[ "${RUN_FULL_API:-0}" == "1" ]]; then
  printf '%s\n' "[next-wave] full API regression"
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore
fi

if [[ "${RUN_FRONTEND:-0}" == "1" ]]; then
  printf '%s\n' "[next-wave] frontend checks"
  npm test --workspace @taslim/web
  npm run lint --workspace @taslim/web
  npx tsc --noEmit -p apps/web/tsconfig.json
  NEXT_PUBLIC_API_URL=https://api.example.test npm run build --workspace @taslim/web
fi

if [[ "${RUN_DB_MIGRATIONS:-0}" == "1" ]]; then
  test -n "${FRESH_DATABASE_URL:-}" || { echo "FRESH_DATABASE_URL is required" >&2; exit 1; }
  printf '%s\n' "[next-wave] disposable EF model/migration checks"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$FRESH_DATABASE_URL"
fi

printf '%s\n' "[next-wave] PASS — bounded autonomous development loop only; no release, merge, spending, or provider enablement"
