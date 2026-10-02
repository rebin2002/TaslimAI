# Taslim.ai Safe Autopilot Controller

## Purpose

The Autopilot Controller is orchestration infrastructure. It coordinates a wave of up to **20 parallel implementation tasks**, consumes completion events, reconciles durable state, and exposes mandatory integration, release, deployment, and smoke-verification transitions. It does not select product direction, pricing, providers, charging, or destructive database actions.

The controller is an **event/webhook-first** system. `AutopilotEvents` is the durable completion queue and every event is keyed by a unique producer `eventId`. The hourly watchdog is only a fallback for lease recovery and reconciliation; it does not rapidly poll or launch a wave.

## Durable state

- `AutopilotWaves`: wave number, production base SHA, integration branch, immutable candidate SHA, deployed revision, bounded retry/concurrency policy, current state, and decision pause details.
- `AutopilotTasks`: task key, branch, base/head SHA, mandatory flag, attempt budget, failure classification, and task state.
- `AutopilotRuns`: task/gate/deploy/smoke attempts with leases and terminal outcomes.
- `AutopilotEvents`: append-only, SHA/branch-aware event records. The unique event ID makes duplicate delivery idempotent. Raw event payloads are not stored; only a SHA-256 payload fingerprint is retained.
- `AutopilotGates`: one integration gate and one final release gate per wave, each with a bounded attempt budget.
- `AutopilotLocks`: durable resource locks with leases for launch, wave transitions, and reconciliation.
- `AutopilotAuditEntries`: bounded, append-only operator/status evidence suitable for ChatGPT checks.

The EF migration `20261002194141_AddAutopilotControllerFoundation` is additive and backward-compatible. No existing tables are altered or dropped.

## Safe Wave 6 launch procedure

Wave 6 must **not** be launched from the controller implementation/release task. After this branch is safely integrated and production health is independently verified:

1. Confirm the production base SHA and integration branch in the launch request. The controller accepts only a 40-character SHA and stores the base SHA on every task.
2. Submit one launch request with at most 20 task records and a unique idempotency key. The controller rejects duplicate wave numbers and conflicting idempotency reuse.
3. Keep the Wave 6 favicon task in the task list; it remains a Wave 6 task and is not included in this foundation.
4. If any task is marked decision-sensitive, the wave starts in `NEEDS_DECISION` and no task event can start until an explicit, reason-matched decision is recorded.
5. Dispatch task work from the returned `PENDING` task records. Each worker emits `TASK_STARTED` and `TASK_COMPLETED` events containing the exact branch and production base SHA.
6. Treat `duplicate` and `ignored` event responses as safe reconciliation outcomes, not as permission to resend with different branch/SHA data.
7. Ordinary task/CI failures are retried only within the task attempt budget. Security-sensitive, product-direction, pricing, ambiguous-business, destructive-schema, provider, or charging failures transition to `NEEDS_DECISION` without automatic retry.
8. Call reconciliation after completion batches or let the hourly watchdog recover expired leases. Reconciliation opens the integration gate only when every mandatory task is `SUCCEEDED`.
9. Record the **integration gate** against the candidate SHA. On pass, that SHA is immutable and the final release gate is opened. A different candidate is rejected.
10. Record the **final release gate** against the exact immutable candidate. Deployment is rejected until this gate is green.
11. Record deployment success with the exact candidate SHA. The wave then enters `SMOKE`.
12. Record production smoke success with the exact deployed revision. Only then does the wave become `COMPLETED`.

## Operator endpoints

All endpoints are protected by the existing `TaslimAdminUsage` policy. Provider credentials, prompts, storage keys, and secrets are not part of the contracts.

- `POST /api/admin/autopilot/waves`
- `GET /api/admin/autopilot/waves/{waveId}`
- `POST /api/admin/autopilot/waves/{waveId}/events`
- `POST /api/admin/autopilot/waves/{waveId}/reconcile`
- `POST /api/admin/autopilot/waves/{waveId}/gates`
- `POST /api/admin/autopilot/waves/{waveId}/deployment`
- `POST /api/admin/autopilot/waves/{waveId}/smoke`
- `POST /api/admin/autopilot/waves/{waveId}/decision`
- `POST /api/admin/autopilot/waves/{waveId}/pause`

The status response includes task counts, task branch/SHA state, gate state, candidate/deployed revisions, current decision/error state, and the most recent bounded audit records.

## Recovery and safety rules

- A duplicate event ID is acknowledged without changing state or creating a second run.
- A stale branch/base SHA event is durably recorded as ignored and cannot advance a task.
- An expired task lease becomes a bounded retry or a terminal failure; restart does not lose the run state.
- Retry exhaustion fails the wave safely. It never creates an unbounded loop.
- Gate, deployment, and smoke transitions are ordered and candidate/revision exact-match checked. No endpoint can skip a mandatory gate.
- Durable locks prevent duplicate launch, transition, or reconciliation work. Leases allow recovery after process termination.
- No force-push or production database reset is used. No destructive migration is auto-approved.
- `Billing.CustomerChargingEnabled` stays `false`. Paid/external generation providers and existing safety flags stay off. The controller does not enable them.
- `Autopilot.Enabled` is `false` by default in committed development and production settings. Enable the hourly watchdog only as an explicit production configuration action after the controller itself passes release review.
