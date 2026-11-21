#!/usr/bin/env bash
set -Eeuo pipefail
readonly repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Static-only contract check: reads checked-in settings/docs/source and never
# invokes curl, a provider, a deployment, or any live infrastructure.
python3 - <<'PY'
import json
from pathlib import Path

for name in ("apps/api/appsettings.json", "apps/api/appsettings.Production.json"):
    settings = json.loads(Path(name).read_text(encoding="utf-8"))
    autopilot = settings["Autopilot"]
    assert autopilot["Enabled"] is False, f"{name}: Autopilot.Enabled must default off"
    assert autopilot["DryRun"] is True, f"{name}: Autopilot.DryRun must default on"
    assert autopilot["SimulationMode"] is True, f"{name}: simulation must default on"
    assert autopilot["ChargingEnabled"] is False, f"{name}: Autopilot charging must default off"
    assert autopilot["PaidProvidersEnabled"] is False, f"{name}: paid providers must default off"
    assert autopilot["AllowNextWaveLaunch"] is False, f"{name}: event refill must default off"
    assert autopilot["BridgeBaseUrl"] == "", f"{name}: bridge endpoint must be unset by default"
    assert settings["Billing"]["CustomerChargingEnabled"] is False, f"{name}: customer charging must default off"

activation = Path("docs/AUTOPILOT_SAFE_ACTIVATION.md").read_text(encoding="utf-8")
for required in (
    "## 5. Kill switch, pause, and resume",
    "POST https://<api>/api/admin/autopilot/control",
    "-d '{\"killSwitch\":true,\"reason\":\"incident 2026-xx-xx\"}'",
    "Set `Autopilot:Enabled=false`",
    "Optionally set `Autopilot:WatchdogEnabled=false`",
    "No data is deleted; runs, gates, handoffs, and audit rows remain for review.",
):
    assert required in activation, f"documented stop/deactivation contract missing: {required}"

controller = Path("apps/api/Controllers/AutopilotConsoleController.cs").read_text(encoding="utf-8")
assert '[Route("api/admin/autopilot")]' in controller
assert '[HttpPost("control")]' in controller
assert "request.KillSwitch" in controller, "control endpoint must forward the kill-switch request"

rollback_doc = Path("docs/RELEASE_EVIDENCE.md").read_text(encoding="utf-8")
for required in (
    "## Rollback use",
    "This artifact is provenance and diagnosis evidence, not a rollback command.",
    "Rollbacks continue through the normal reviewed deployment process.",
    "Never reset, drop, or recreate a production database",
):
    assert required in rollback_doc, f"rollback guidance missing: {required}"

release_script = Path("scripts/release-evidence.sh").read_text(encoding="utf-8")
assert "This gate does not deploy, contact production, apply migrations, or activate" in release_script

print("Autopilot staging/rollback contract checks passed (static-only; no live calls).")
PY
