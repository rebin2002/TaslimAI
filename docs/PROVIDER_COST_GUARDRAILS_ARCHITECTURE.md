# Provider Cost Estimation and Generation Budget Guardrails

## Scope

Taslim records **internal provider exposure only**. The feature does not calculate customer prices, deduct customer credits, or enable payment-provider activity. Normal generation-job DTOs continue to omit provider, model, pricing, estimate, and ledger details. Safe cost-knowledge fields are limited to the existing protected administrator usage surface.

## Typed estimation contract

`GenerationCostEstimationRequest` is provider-neutral and accepts nullable usage dimensions: input, cached-input, and output tokens; image input/output tokens and image generations; voice duration and characters; music duration; and video duration. `GenerationCostEstimate` contains an explicit `IsKnown` flag, nullable `AmountUsd`, a version/effective-date/source snapshot, and component-level rates. A missing provider rule or missing rate produces `AmountUsd = null` and an explicit reason; it is never converted to zero.

`GenerationCostPricingOptions` is bound from `GenerationCostPricing`. Provider/model rules are configuration data rather than permanent business logic. The empty default catalog is intentional and the guardrail is fail-closed when enabled. Existing model and media pricing options were also changed to nullable rates; configured rates must carry a version and source before a pricing snapshot is emitted.

## Execution guardrails

`GenerationBudgetOptions` controls the internal safety envelope without changing customer entitlements:

| Setting | Enforcement |
| --- | --- |
| `MaxEstimatedCostPerJobUsd` | Rejects an attempt whose estimate exceeds the per-job ceiling. |
| `MaxProviderAttemptsPerJob` | Rejects a new retry/fallback attempt after the configured count. |
| `MaxCumulativeEstimatedCostPerJobUsd` | Includes all known estimates for prior attempts of the job. |
| `WorkspaceInternalSafetyCeilingUsd` | Includes known attempted exposure for the workspace. |
| `UserInternalSafetyCeilingUsd` | Includes known attempted exposure for the creating user. |
| `RejectUnknownEstimates` | Rejects unknown estimates while guardrails are enabled; the safe default is `true`. |

`GenerationProviderAttempt` is append-only in intent and has unique `(GenerationJobId, AttemptNumber)` and `FinalizationKey` constraints. It records the estimate snapshot, whether estimated and actual cost are known, provider/model identifiers, status, failure code, and terminal time. This is operational/audit data and is not exposed to ordinary users.

## Exactly-once ledger behavior

A generation job still maps to one idempotent usage request key: `generation:{jobId}`. Retries and fallbacks create provider-attempt audit rows but do not create additional final Usage Ledger transactions. `UsageLedgerService` now treats `Completed`, `Refunded`, `Failed`, and `Cancelled` as terminal for completion/failure/cancellation calls. A late completion after cancellation therefore cannot reopen the transaction or finalize it twice. The generation service also prevents a terminal generation transaction from being reset to `Pending` by a later begin call.

Estimated provider cost is stored separately from provider cost. `ProviderCostKnown` distinguishes a real zero from an unknown value represented by the legacy numeric storage field, while `CostBasis` distinguishes `Actual`, `Estimated`, and `Unknown`. Customer `ChargedAmount` remains zero through `SafeUsageChargingService`.

## Configuration and migration

The additive migration `20260925143000_AddGenerationCostGuardrails` adds generation-job estimate metadata, usage cost-knowledge and estimate-audit fields, and the `GenerationProviderAttempts` table with indexes and a restrictive audit-friendly relationship. `GenerationBudget:Enabled` is `false` in both base and production configuration. `GenerationCostPricing:Providers` is empty by default. Existing `Billing:CustomerChargingEnabled` remains `false` and the provider remains `unconfigured`.

## Tests

Focused API tests cover configured/unknown estimates, token/media/fixed dimensions, per-job and cumulative retry ceilings, provider-attempt ceilings, workspace/user ceilings, duplicate completion, late completion after cancellation, zero customer charge, and no credit-ledger deduction. `git diff --check` and JSON configuration validation passed. The environment does not contain the .NET SDK (`dotnet` is unavailable), so `dotnet build`/`dotnet test` could not be executed here; no Playwright or deployment was run.
