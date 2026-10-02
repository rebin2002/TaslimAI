# Autopilot Controller — Safe Activation and Human Decision Boundary

This runbook explains how to activate the Autopilot Controller foundation safely, and exactly where the boundary
between automatic action and human-required decisions sits.

The controller ships **off**: `Autopilot:Enabled = false` and `Autopilot:DryRun = true`. Nothing in this document
authorizes a wave launch or a production release; those remain human decisions.

## 1. Default posture

| Setting | Shipped value | Why |
| --- | --- | --- |
| `Autopilot:Enabled` | `false` | No orchestration until an operator enables it |
| `Autopilot:DryRun` | `true` | Every decision is evaluated and recorded, no external action |
| `Autopilot:ChargingEnabled` | `false` | Charging stays off |
| `Autopilot:PaidProvidersEnabled` | `false` | Paid/external providers stay off |
| `Autopilot:AllowAutomaticIntegrationMerge` | `false` | Merging needs a human |
| `Autopilot:AllowAutomaticRelease` | `false` | Releasing needs a human |
| `Autopilot:RequireSignedEvents` | `true` | Unsigned intake is rejected |
| `Autopilot:MaxConcurrency` | `20` | Bounded fan-out |

Production startup refuses to proceed if charging or paid providers are enabled, if signed events are disabled,
or if the signing secret is missing while the controller is enabled. The Stage 1 posture (`Enabled=true` with
`DryRun=true`) is a supported production configuration; live execution additionally requires `DryRun=false` to be
set explicitly.

## 2. What is automatic vs. human-required

### Proceeds automatically

| Action | Conditions |
| --- | --- |
| Intake, dedupe, replay rejection | Always (signature and identity verified) |
| Recording run/task state and audit entries | Always |
| Retrying `ordinary` and `infrastructure` failures | Bounded by `MaxTaskAttempts` with capped backoff |
| Inspecting wave task results | Always |
| Evaluating the integration gate and the final release gate | Always; a failed gate stops the wave |
| Preparing a release handoff record | Always, but the handoff is not executed in dry-run |
| Watchdog reconciliation of missed events | Bounded by `MaxWatchdogReconciliations` |
| Recording next-wave eligibility | Always; the wave is never launched by the controller |

### Requires a human decision

| Decision | Trigger |
| --- | --- |
| `pricing` | Pricing or cost-model changes (`failureClass: pricing`) |
| `destructive_schema` | Irreversible/destructive schema change (`failureClass: schema_irreversible`) |
| `security_ambiguity` | Security-sensitive ambiguity (`failureClass: security`) |
| `product_direction` | Major product-direction ambiguity (`failureClass: product_direction`) |
| `enable_charging` | Enabling charging or paid providers |
| `enable_paid_providers` | Enabling paid/external generation providers |
| `production_release` | Any live production release |
| `integration_merge` | Merging the wave candidate into main |
| `repair_escalation` | A retry-exhausted ordinary failure, or a blocking class that is not auto-repairable |

### Never performed, at any setting

* `force_push`
* `reset_production_database`
* `drop_production_data`
* `disable_charging_guard`

These are encoded in `AutopilotForbiddenActions` and `AutopilotSafetyPolicy.CanPerformDestructiveDatabaseAction`,
which always returns `false`.

## 3. Preconditions before enabling

1. A completion bridge can deliver events to `POST /api/autopilot/events`.
2. `AUTOPILOT_WEBHOOK_SECRET` is set server-side in the API environment (never in the repository, never in a
   client build).
3. The bridge sends `X-Autopilot-Source`, `X-Autopilot-Event-Id`, `X-Autopilot-Event-Type`,
   `X-Autopilot-Timestamp`, and `X-Autopilot-Signature` (`sha256=<hex HMAC of "{timestamp}.{payload}">`).
4. The migration `20261002100839_AddAutopilotControllerFoundation` is applied.
5. The administrator role (`TaslimAdministrator`) exists for the operator who will watch the console.

## 4. Staged activation

### Stage 0 — deploy dark (default)

Deploy with `Autopilot:Enabled=false`. Intake still authenticates, deduplicates, and queues events durably, so
the bridge can be validated without any orchestration. Verify in the console that events arrive and no run
appears.

### Stage 1 — observe in dry-run

Set `Autopilot:Enabled=true` and keep `Autopilot:DryRun=true`. Every gate is evaluated and every decision is
audited, but the handoff and smoke are simulated. Use this to confirm that waves complete, gates pass or fail for
the expected reasons, and retries behave.

This posture boots in production: `ProductionConfigurationValidator` requires signed events and the server-side
signing secret while the controller is enabled, and it permits `DryRun=true`. Nothing external is executed in this
stage — charging, paid providers, automatic integration merge, and automatic release all remain off, and the
release handoff is recorded but never carried out.

Watch:
* `GET /api/admin/autopilot/overview`
* `GET /api/admin/autopilot/events`
* `GET /api/admin/autopilot/audit?waveKey=<wave>`

### Stage 2 — supervised live

Only after Stage 1 is clean, and only with an explicit human decision, set `Autopilot:DryRun=false`. Keep
`AllowAutomaticRelease=false` and `AllowAutomaticIntegrationMerge=false`: the controller will prepare the handoff
and park at `handoff_pending_human` with `production_release` required. A human authorizes each release.

### Stage 3 — unattended release (separate, explicit decision)

`Autopilot:AllowAutomaticRelease=true` is a deliberate, separately reviewed decision. Charging and paid providers
remain off regardless.

## 5. Kill switch, pause, and resume

```bash
# Pause all orchestration (events keep queuing durably)
curl -X POST https://<api>/api/admin/autopilot/control \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: <token>" \
  --cookie 'taslim.auth=<session>' \
  -d '{"paused":true,"reason":"investigating gate noise"}'

# Hard kill switch (no orchestration and no reconciliation at all)
curl -X POST https://<api>/api/admin/autopilot/control \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: <token>" \
  --cookie 'taslim.auth=<session>' \
  -d '{"killSwitch":true,"reason":"incident 2026-xx-xx"}'

# Resume
curl -X POST https://<api>/api/admin/autopilot/control \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: <token>" \
  --cookie 'taslim.auth=<session>' \
  -d '{"paused":false,"killSwitch":false,"reason":"resolved"}'
```

A reason is mandatory and every change is audited as `control.changed`. To stop everything regardless of operator
access, set `Autopilot:Enabled=false` and redeploy, or set `Autopilot:WatchdogEnabled=false` to stop only the
fallback.

## 6. Manual reconciliation

If the bridge loses an event, run one bounded reconciliation pass instead of waiting for the hourly watchdog:

```bash
curl -X POST https://<api>/api/admin/autopilot/reconcile \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: <token>" \
  --cookie 'taslim.auth=<session>' \
  -d '{"reason":"bridge gap 2026-xx-xx"}'
```

The response reports how many events were examined, processed, and skipped.

## 7. Verifying a wave decision

* `overview` returns the control state, the run state, per-task states, both gate evaluations, the handoff record,
  and recent audit entries.
* A blocked wave always carries `humanDecisionRequired` with the exact decision category.
* `audit` shows the reason, status, attempt, task id, branch, base SHA, and candidate SHA for every decision.
* Nothing in these responses contains provider, model, or prompt names.

## 8. Incident playbook

| Symptom | Action |
| --- | --- |
| Duplicate integrations/releases suspected | Engage the kill switch, then inspect `AutopilotLocks` and `AutopilotGateEvaluations`; the unique indexes guarantee at most one gate row per `(run, gate, candidate SHA)` |
| Wave stuck in `awaiting_events` | Check the declared `expectedTaskCount`; run a manual reconciliation |
| Wave stuck in `integration_failed` | Read the failed check names on the gate row; fix, then deliver a new completion event for the candidate |
| Retry storm | Lower `Autopilot:MaxTaskAttempts`, raise `RetryBaseDelaySeconds`, or engage the kill switch |
| Wrong SHA recorded | It cannot be changed: the run's SHA is immutable by design. Start a new wave key |
| Signing failures | Verify the secret environment variable and the bridge clock skew (default tolerance 300 s) |

## 9. Deactivation

1. Set `Autopilot:Enabled=false` (events continue to queue durably for later analysis).
2. Optionally set `Autopilot:WatchdogEnabled=false`.
3. Optionally engage the kill switch for an explicit operational record.
4. No data is deleted; runs, gates, handoffs, and audit rows remain for review.

## 10. Change control for this boundary

Changing any of the following requires a human decision and a documented review, not an autonomous action:
`AllowAutomaticIntegrationMerge`, `AllowAutomaticRelease`, `ChargingEnabled`, `PaidProvidersEnabled`,
`RequireSignedEvents`, and the forbidden-action list.
