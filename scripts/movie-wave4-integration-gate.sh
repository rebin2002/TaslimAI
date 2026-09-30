#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
RUN_BROWSER_E2E="${RUN_BROWSER_E2E:-0}"
RUN_DB_MIGRATIONS="${RUN_DB_MIGRATIONS:-0}"
REQUIRED_BASE="4142f533b4c2474586f18aee12025295c56a549d"

step() { printf '\n==> %s\n' "$1"; }
require_command() { command -v "$1" >/dev/null 2>&1 || { printf 'Missing required command: %s\n' "$1" >&2; exit 1; }; }

step "Toolchain"
require_command dotnet
require_command npm
require_command npx
require_command python3
require_command git

step "Base and repository hygiene"
git fetch origin main --quiet --prune
git cat-file -e "$REQUIRED_BASE^{commit}"
git merge-base --is-ancestor "$REQUIRED_BASE" origin/main
git merge-base --is-ancestor origin/main HEAD || {
  printf 'This branch is not based on the current origin/main tip.\n' >&2
  exit 1
}
printf 'Required release is an origin/main ancestor: %s\n' "$REQUIRED_BASE"
git diff --check
if grep -RInE --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=.next --exclude-dir=coverage --exclude-dir=test-results --exclude-dir=playwright-report '^(<<<<<<<|=======|>>>>>>>)' .; then
  printf 'Conflict markers found.\n' >&2
  exit 1
fi
printf 'Conflict-marker audit: clean\n'

step "Provider, charging, and tracked-secret safety"
python3 - <<'PY'
import json
from pathlib import Path

checks = [
    (Path("apps/api/appsettings.json"), ("Billing", "CustomerChargingEnabled"), False),
    (Path("apps/api/appsettings.json"), ("MovieVideo", "Enabled"), False),
    (Path("apps/api/appsettings.Production.json"), ("Billing", "CustomerChargingEnabled"), False),
]
for path, keys, expected in checks:
    data = json.loads(path.read_text())
    current = data
    for key in keys:
        current = current.get(key)
    if current is not expected:
        raise SystemExit(f"{path}: {'.'.join(keys)}={current!r}; expected {expected!r}")
    print(f"{path}: {'.'.join(keys)}={current!r}")

production = json.loads(Path("apps/api/appsettings.Production.json").read_text())
movie = production.get("MovieVideo")
if movie is not None and movie.get("Enabled") is not False:
    raise SystemExit("Production MovieVideo.Enabled must be false when the section is present")
for section in ("MusicGeneration", "VoiceGeneration"):
    configured = production.get(section, {})
    if configured.get("Enabled") is True:
        raise SystemExit(f"Production {section}.Enabled must remain false for the Wave 4 prep gate")
print("Production MovieVideo, MusicGeneration, and VoiceGeneration: absent or disabled")
PY

if git ls-files | grep -E '(^|/)(\.env|.*\.pem|.*\.p12|.*\.key)$' | grep -v '\.example$'; then
  printf 'Potential secret file is tracked.\n' >&2
  exit 1
fi
printf 'Tracked-secret filename audit: clean\n'

step "API build"
dotnet build apps/api/Taslim.Api.csproj

step "Wave 4 deterministic acceptance"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~MovieWave4'

step "Wave 3 integration regression"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~MovieWave3Integration'

step "API regression tests"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore

step "Frontend unit tests"
npm test --workspace @taslim/web

step "TypeScript"
npx tsc --noEmit -p apps/web/tsconfig.json

step "ESLint"
npm run lint --workspace @taslim/web

step "Production frontend build"
NEXT_PUBLIC_API_URL="${NEXT_PUBLIC_API_URL:-https://api.example.test}" npm run build --workspace @taslim/web

if [[ "$RUN_DB_MIGRATIONS" == "1" ]]; then
  step "EF model consistency"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api
  if [[ -z "${FRESH_DATABASE_URL:-}" || -z "${UPGRADE_DATABASE_URL:-}" ]]; then
    printf 'RUN_DB_MIGRATIONS=1 requires FRESH_DATABASE_URL and UPGRADE_DATABASE_URL.\n' >&2
    exit 1
  fi
  step "Fresh PostgreSQL migration"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$FRESH_DATABASE_URL"
  step "Upgrade PostgreSQL migration"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$UPGRADE_DATABASE_URL"
else
  printf '\nDatabase migration checks skipped. No Wave 4 migration is expected on this prep branch. Set RUN_DB_MIGRATIONS=1 with two disposable PostgreSQL URLs for the base-path audit.\n'
fi

if [[ "$RUN_BROWSER_E2E" == "1" ]]; then
  step "Full browser E2E"
  npm run test:e2e --workspace @taslim/web
else
  printf '\nBrowser checks skipped. Set RUN_BROWSER_E2E=1 with a disposable API/database to run browser E2E.\n'
fi

step "Gate complete"
printf '%s\n' 'Wave 4 acceptance, Wave 3 regression, API, frontend, type, lint, build, repository, and safety checks passed for this checkout.'
printf '%s\n' 'No branch merge, deployment, provider enablement, customer charging, or paid/external generation action is performed by this script.'
