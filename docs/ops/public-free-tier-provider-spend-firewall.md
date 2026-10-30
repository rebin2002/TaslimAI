# Public free-tier provider-cost firewall — integration handoff

**Status: P0 pre-public-beta gate; NOT integrated; NOT deployable as a firewall.** This isolated owner-safe PR adds a pure, fail-closed policy, a reservation-ledger contract with deterministic tests, and a standalone quota/unavailable notice with render tests. It does not register the policy in the API, mount the notice in any page, change routes/workers, create a durable database store, alter production configuration, or protect any currently reachable provider call. Keep public free-tier generation on hold until the integration and release gates below are completed on an owner-approved, exact reviewed HEAD.

## Why this gate exists

At the reviewed `main` HEAD (`c7622a13142ba85c3009955fbfafb2af0469f903`), the API production configuration has `Billing.CustomerChargingEnabled=false`, `UsageControls.GuardrailsEnabled=false`, and `GenerationBudget.Enabled=false`. Production `GenerationCostPricing` has no provider rates (`Version`, `EffectiveAtUtc`, and `Source` are null and `Providers` is empty). The seeded Free plan's 1,000 monthly customer credits are not provider-cost funds. Disabling customer debits therefore does not prevent a visitor's request from reaching a paid provider; with missing pricing, this policy must reject rather than infer zero cost.

The live route surface includes Chat/SSE, image, research, document, presentation, social, music, voice, video, Movie, upscaling, and background job execution. A gate only in a UI, controller preflight, or job-creation path is insufficient: every provider attempt, retry, SSE call, callback-triggered action, and worker execution needs a reservation immediately before dispatch.

## What this change supplies

`apps/api/Usage/PublicFreeTierProviderSpendPolicy.cs` defines an internal server-side admission request, a UTC-window exposure snapshot, a pure policy evaluator, and the `IPublicFreeTierSpendLedger` contract. The policy is off by default and returns service-unavailable when disabled or misconfigured; it never fails open. It requires an authenticated active account, confirmed email by default, a server-verified active workspace membership, and the server-derived Free plan. The request identity/plan/workspace/capability are not HTTP-bindable inputs.

Conservative defaults are 3 chat messages per user per UTC day, 10 per UTC ISO week, 2,000 input tokens and 512 output tokens per request, and estimated-cost ceilings of $0.10/user/day, $0.50/user/month, $0.25/workspace/day, $1/workspace/month, $1/global/day, and $5/global/month. Only `TextChat` is allowlisted by default. The implementation hard-denies image, video, voice, music, short-form clips, background provider jobs, unknown, and arbitrary provider routes even if they are accidentally added to configuration; only code-supported chat or low-cost lookup capabilities can be considered for later allowlisting after separate pricing, bounds, route coverage, tests, and review. These dollar amounts constrain estimates/reservations, not guaranteed provider invoices or usage charges.

The pure evaluator requires versioned USD pricing, matching fresh UTC-window/scope snapshots, and zero unknown-cost records. It bounds token input/output, returns explicit 401/403/413/429/503 decisions and UTC retry times, and performs overflow-safe spend comparisons. The user message/spend exposure is intended to aggregate across every workspace for that user; the global exposure spans every free account. Customer credits remain a separate ledger and are deliberately absent from the policy.

`apps/api.Tests/PublicFreeTierProviderSpendPolicyTests.cs` tests the policy and exercises the reservation contract with a test-only lock-based in-memory fake. That fake demonstrates intended atomic behavior, user-wide workspace aggregation, retry idempotency, unknown-cost holds, and settlement/refund semantics; it is **not** a production adapter and does **not** prove PostgreSQL, process, or multi-replica correctness. These tests make no paid-provider or network calls.

`apps/web/src/components/PublicFreeTierSpendNotice.tsx` and its focused test render accessible quota/reset and temporary-unavailability copy. The component deliberately accepts only a server-classified state and optional UTC retry time, makes no claim that customer credits were or were not deducted, and is **not mounted into an existing route or page** in this PR.

## Required integration before any free-tier traffic is enabled

1. **Reserve file ownership and integration order.** This PR deliberately does not edit claimed/high-conflict files. The 2026-10-30 inventory scanned all 443 open PRs and retrieved changed-file lists for all of them without path-list errors. All open PRs were authored by `rebin2002`; no open PR had an assignee or requested reviewer. Direct relevant overlaps include:
   - #668: `apps/api/Usage/GenerationCostGuardrails.cs` and `apps/api.Tests/GenerationCostGuardrailScopeTests.cs`.
   - #519/#520: `apps/api/Usage/GenerationBudgetGuardrails.cs` and `apps/api.Tests/GenerationBudgetProjectCapTests.cs`.
   - #319/#390: `apps/api/Usage/UsageLedgerService.cs` and `apps/api.Tests/UsageAccountingUnitTests.cs` (overlapping Usage-ledger work).
   - #109: `apps/api/Billing/BillingServices.cs`, `apps/api.Tests/BillingTests.cs`, `apps/api/Program.cs`, production/app settings, and other code; its billing-file transfer request remains unaccepted per the release handoff.
   - #686/#66 and other PRs touch `apps/api/Program.cs`, auth, or app settings. Many open PRs also touch generation controllers, provider handlers, and job workers.

   Do not rebase into or edit those owners' files absent a written path disposition and integration sequence from the release coordinator. This PR adds only the five exact reserved new files: `apps/api/Usage/PublicFreeTierProviderSpendPolicy.cs`, `apps/api.Tests/PublicFreeTierProviderSpendPolicyTests.cs`, `apps/web/src/components/PublicFreeTierSpendNotice.tsx`, `apps/web/src/components/PublicFreeTierSpendNotice.test.tsx`, and `docs/ops/public-free-tier-provider-spend-firewall.md`.

2. **Implement the durable cross-replica spend store.** Implement `IPublicFreeTierSpendLedger` using durable shared storage (not process memory). In one database transaction/atomic operation, query the canonical user-wide, workspace-wide, and global daily/monthly exposure; include outstanding reservations and settled actuals; evaluate the policy; and write a unique, durable reservation before returning success. Use row locks/serializable transaction or equivalent atomic conditional counters that are verified on the actual production database engine. A stale caller-supplied exposure snapshot, per-instance cache, in-memory limiter, or eventually consistent aggregate cannot authorize a provider call. If a schema migration is needed, submit it separately with migration review, backup, restore rehearsal, and rollback gates; this PR has no migration.

3. **Bind identity and route facts on the server.** Build the admission request only from the authenticated principal, canonical active subscription/Free-plan state, confirmed email, and `WorkspaceAccessService` authorization. The selected `UserId`, workspace, plan, capability, pricing version, and idempotency key must not be trusted from query/body/header values. This firewall has no administrator/beta/query-string exemption. Any later internal exception needs a distinct explicit server-side authorization decision and independent review.

4. **Gate every outbound attempt.** Integrate immediately before external dispatch for the complete provider inventory: Chat including SSE and regeneration; image; Research/Search providers; document/presentation/social; voice; music; video/Movie and clip jobs; upscaling; all provider retries/fallbacks; and background workers/callback-triggered work. Reserve before dispatch and pass the policy's maximum output-token limit to the provider. Queueing a job or passing a UI preview is not a reservation. Add route/worker coverage that fails if a paid provider path lacks the gate; prove blocked requests cause zero provider-client invocations. Keep every unpriced capability default-denied.

5. **Idempotency and accounting semantics.** Generate server-side idempotency keys bound to user, authorized workspace, logical operation and provider-attempt number. A replay of the same attempt returns its existing reservation; a different payload under the same key conflicts. A retry/fallback is a new provider attempt and needs a new reservation before dispatch. On completion, reconcile estimate to actual. Known failed-provider cost remains charged as provider exposure; release an estimate only when the outbound request is proven never dispatched. If actual usage is unknown, retain the full reserved estimate and mark reconciliation required; fail closed for new public free work until resolved. Make all finalization/refund/reconciliation operations idempotent. Never use customer-credit refund logic as a provider-cost release.

6. **Registration and abuse defenses.** The current registration endpoint has the `Authentication` rate-limit annotation. The current policy is a 60-request/minute in-process limiter partitioned by authenticated user or, for anonymous requests, `Connection.RemoteIpAddress`; its global concurrency limiter is also process-local. `Program.cs` currently consumes forwarded scheme only (`X-Forwarded-Proto`), explicitly not forwarded client IP. Therefore these existing limits are not durable per-account/per-workspace daily limits and may share an ingress IP partition; do not trust arbitrary `X-Forwarded-For` to improve them. Before public beta, have the infrastructure/security owner decide and test a proxy-safe registration edge limit, email verification and/or invite/CAPTCHA control, plus account/workspace creation limits. Use only the actual connection address or forwarding values from a specifically trusted proxy chain; do not allow spoofed headers to grant extra quota.

7. **User feedback and HTTP behavior.** Map policy decisions to a stable error envelope; for 429 responses send `Retry-After` from the UTC retry time and show a truthful free-tier quota message. For disabled, unknown-price, stale-accounting, or reconciliation-required decisions, say the capability is temporarily unavailable—not that credits were deducted or that the user should retry repeatedly. The standalone notice and render tests in this PR are not mounted; the current front-end may still expose unavailable studios. Localize and integrate the copy on owner-approved pages without making client-side access checks authoritative.

8. **Provider-side budget and independent review.** Configure hard project-level provider spend limits/alerts as a second layer and verify their reset period/currency with the provider. The proposed $1/day and $5/month limits are estimates; actual provider charges can differ. Require formal independent security and accounting review, exact-head green CI, provider-free and PostgreSQL-backed concurrency/idempotency/reconciliation tests, complete route/worker coverage, and explicit operator/user approval before enabling public free generation. The release coordinator reported no distinct eligible independent reviewer in the current collaborator listing; resolve that blocker rather than self-approving.

## Railway configuration guide (future integration only)

This PR does not bind these options in `Program.cs`, add environment variables, connect to Railway, or change production. Do **not** treat setting a Railway variable as activating a firewall until the durable adapter and every route/worker gate above are merged through the owner's approved integration sequence.

After the reviewed integration exists, keep the first Railway staging rollout disabled by default:

```text
PublicFreeTier__Enabled=false
PublicFreeTier__RequireConfirmedEmail=true
PublicFreeTier__MaxChatMessagesPerUserPerUtcDay=3
PublicFreeTier__MaxChatMessagesPerUserPerUtcWeek=10
PublicFreeTier__MaxInputTokens=2000
PublicFreeTier__MaxOutputTokens=512
PublicFreeTier__MaxProviderSpendPerUserPerUtcDayUsd=0.10
PublicFreeTier__MaxProviderSpendPerUserPerUtcMonthUsd=0.50
PublicFreeTier__MaxProviderSpendPerWorkspacePerUtcDayUsd=0.25
PublicFreeTier__MaxProviderSpendPerWorkspacePerUtcMonthUsd=1.00
PublicFreeTier__MaxGlobalProviderSpendPerUtcDayUsd=1.00
PublicFreeTier__MaxGlobalProviderSpendPerUtcMonthUsd=5.00
PublicFreeTier__MaxExposureSnapshotAgeSeconds=15
```

Enable only on an isolated staging service with fake providers and the shared durable database after a reviewed test run. First verify that an unknown capability, an unknown cost, storage unavailability, and a stale snapshot reject with zero provider dispatch. Then verify cross-workspace user quotas, all global caps under concurrent workers/replicas, provider failures, retries, duplicate idempotency keys, actual-cost reconciliation, and the exact 429/error UI. Production enabling requires the formal independent reviews, provider-side project budget, exact release evidence, operator approval, and explicit user approval. Never put provider keys or credentials in code or this guide.

## Release disposition

This code is a policy/contract handoff only; it does not establish an active server-side firewall or a safe public launch. The current release state remains **HOLD / NO-GO for public free AI** until all integration gates pass. No production API, Railway setting, credential/secret, customer balance, provider, database, migration, branch other than this new isolated branch, existing PR, merge, or deployment was changed by this handoff.
