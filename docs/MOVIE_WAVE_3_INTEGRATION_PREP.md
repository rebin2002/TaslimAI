# Taslim AI — Movie Wave 3 Integration Preparation

**Branch:** `parallel/movie-wave3-integration-prep`
**Base:** `308e146aa73e91443097229a81f97dac008b92ee` (`origin/main`)
**Scope:** integration matrix, conflict map, safe gate, compatibility seam, and deterministic acceptance only.

> No Wave 2 or Wave 3 implementation branch is merged here. No deployment, provider enablement, customer charging, or paid generation is performed.

The machine-readable source of truth is [`movie-wave3-integration-matrix.json`](./movie-wave3-integration-matrix.json). The separate conflict register is [`MOVIE_WAVE_3_CONFLICT_MAP.md`](./MOVIE_WAVE_3_CONFLICT_MAP.md).

## 1. Current-base decision

`origin/main` contains the Movie V2 hierarchy, production versions, takes, explicit selection/finalization, quality-control boundary, generation-job usage ledger, provenance JSON, and disabled Movie video defaults. It does **not** contain the audited Wave 2 shot-production/complexity/importance integration or the Wave 3 adaptive-resolution, benchmark, upscale, and mastering migrations.

This branch therefore adds only `MovieWave3IntegrationCompatibility`: a provider-neutral seam for deterministic resolution recommendation, economical draft selection, explicit QC escalation, unknown-cost representation, and selected-take-only eligibility. The seam has no database tables, controllers, provider calls, prompts, model identifiers, or billing behavior. It is intentionally replaceable when the integrated contracts land.

## 2. Integration order

1. Reconcile and land the Wave 2 shot-production, complexity, importance, runtime, and cinematography contracts first. Do not use a Wave 3 branch tip as a substitute for that dependency.
2. Regenerate one composed EF model snapshot and validate fresh and upgrade PostgreSQL migration paths.
3. Land the provider-neutral video adapter/capability boundary before any executable provider adapter. Keep provider readiness false in the acceptance environment.
4. Land adaptive-resolution request/recommendation contracts and map them to the compatibility seam. Preserve target/source resolution as product intent, not a vendor decision.
5. Land benchmark persistence and fixture replay. Benchmark observations may inform a later governed decision but must not silently select a provider or change a user's shot.
6. Land upscaling and selected-take audit behavior. Re-check the server-side selected/final pointer at execution time; candidate takes must remain blocked.
7. Land final mastering/QC orchestration. A recorded request or pending hand-off is not a completed master and must not publish an Asset or charge usage.
8. Reconcile the browser and API acceptance against final DTOs, then run the complete gate with disposable database URLs only.

## 3. Acceptance coverage

`MovieWave3IntegrationE2ETests` and `MovieWave3IntegrationPrepTests` cover the following without real media providers:

- deterministic adaptive resolution recommendation;
- economical draft source path;
- multiple persisted takes and explicit selection/finalization;
- selected-take-only upscale eligibility;
- low-confidence QC escalation;
- explicit unknown cost estimate (not a customer charge);
- provider-disabled movie job failure with `Failed` usage and `ChargedAmount=0`;
- reload of selected/final take pointers and stage provenance;
- Movie video provider readiness false and customer charging false.

The current-base E2E uses the existing test-only fake adapter only for the persistence/selection journey. It never uses a real provider, credential, network generation call, or paid path. The safety E2E uses the production `UnavailableMovieVideoProvider` registration and verifies the failed-job accounting path.

## 4. Dependencies intentionally not hidden

| Dependency | Current base | Wave 3 expectation | Handling here |
|---|---|---|---|
| Wave 2 shot production contract | Partial | Stable nullable, provider-neutral shot inputs | Explicit dependency; no branch merge |
| Complexity / importance assessments | Absent | Inputs to recommendation, not routing authority | Compatibility seam accepts bounded scores only |
| Runtime / cinematography | Foundation | Inputs retained without silent duration scaling | Existing contracts and focused sentinels |
| Benchmark persistence | Absent | Re-runnable observations with provenance/evidence | Matrix records migration and snapshot risk |
| Upscale audit / executor | Absent | Selected/final ready take only; audit before execution | Compatibility evaluator + future branch mapping |
| Final mastering / QC persistence | Absent | Provider-neutral hand-off, validated output before publish | Matrix dependency; no fake completion |

## 5. Safety invariants

- `apps/api/appsettings.json` and `apps/api/appsettings.Production.json` keep `Billing.CustomerChargingEnabled=false`.
- Movie video remains disabled in normal configuration; the gate rejects an enabled production Movie video section.
- Normal-user contracts contain product intent only. Provider/model/prompt/credential details stay internal to future adapter and benchmark boundaries.
- Failed, unavailable, cancelled, and QC-rejected work publishes no partial Asset and charges zero.
- The gate never drops a database, points at production, deploys, enables a provider, or enables charging.

## 6. Verification

Safe default:

```bash
./scripts/movie-wave3-integration-gate.sh
```

Optional disposable-environment checks:

```bash
RUN_DB_MIGRATIONS=1 \
FRESH_DATABASE_URL='...' \
UPGRADE_DATABASE_URL='...' \
./scripts/movie-wave3-integration-gate.sh

RUN_BROWSER_E2E=1 ./scripts/movie-wave3-integration-gate.sh
```

The gate reports skipped optional stages rather than pretending that migrations or browser E2E ran. The final integrated branch must additionally run the Wave 2 migration upgrade and the Wave 3 branch-specific migration tests listed in the JSON matrix.
