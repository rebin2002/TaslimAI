#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

RUN_BROWSER_E2E="${RUN_BROWSER_E2E:-0}"
RUN_DB_MIGRATIONS="${RUN_DB_MIGRATIONS:-0}"

step() {
  printf '\n==> %s\n' "$1"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Missing required command: %s\n' "$1" >&2
    exit 1
  }
}

step "Toolchain"
require_command dotnet
require_command npm
require_command npx
require_command python3
require_command git

step "Repository hygiene"
git diff --check
if grep -RInE --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=.next --exclude-dir=coverage --exclude-dir=test-results --exclude-dir=playwright-report '^(<<<<<<<|=======|>>>>>>>|\|\|\|\|\|\|\|)' .; then
  printf 'Conflict markers found.\n' >&2
  exit 1
fi
printf 'Conflict-marker audit: clean\n'

step "Charging and Movie video provider safety"
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

production_movie_video = json.loads(Path("apps/api/appsettings.Production.json").read_text()).get("MovieVideo")
if production_movie_video is not None and production_movie_video.get("Enabled") is not False:
    raise SystemExit("Production MovieVideo.Enabled must be false when the section is present")
print("Production MovieVideo: absent or disabled")
PY

step "API build"
dotnet build apps/api/Taslim.Api.csproj

step "API and Movie regression tests"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore

step "Creative fallback, output validation, authorization, provider, and charging regressions"
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~MovieDirectorCreativeOutputValidationTests|FullyQualifiedName~MovieDirectorStoryFailureTests|FullyQualifiedName~MovieAuthorizationPolicyTests|FullyQualifiedName~MovieVideoProviderTests|FullyQualifiedName~BillingTests'

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
    printf 'RUN_DB_MIGRATIONS=1 requires both FRESH_DATABASE_URL and UPGRADE_DATABASE_URL.\n' >&2
    exit 1
  fi

  step "Fresh PostgreSQL migration"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$FRESH_DATABASE_URL"

  step "Upgrade PostgreSQL migration"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$UPGRADE_DATABASE_URL"
else
  printf '\nDatabase migration checks skipped. Set RUN_DB_MIGRATIONS=1 with two disposable PostgreSQL URLs to run them.\n'
fi

if [[ "$RUN_BROWSER_E2E" == "1" ]]; then
  step "Full browser E2E"
  npm run test:e2e --workspace @taslim/web

  step "Dedicated Last Seed acceptance"
  npx playwright test e2e/last-seed-acceptance.spec.ts --config apps/web/playwright.config.ts --project=chromium
else
  printf '\nBrowser checks skipped. Set RUN_BROWSER_E2E=1 with a disposable API/database to run full E2E and Last Seed acceptance.\n'
fi

step "Gate complete"
printf '%s\n' 'API, frontend, type, lint, build, safety, and repository checks passed for this checkout.'
printf '%s\n' 'No deploy, provider enablement, or customer charging action is performed by this script.'
