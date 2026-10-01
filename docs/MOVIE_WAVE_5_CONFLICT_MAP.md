# Movie Studio Wave5 Integration Prep — Conflict Map

**Branch:** `parallel/movie-wave5-integration-prep`
**Base:** `d87ee7fc60c92d4b6a64d37762cdb21418652e07`
**Scope:** preparation only. This branch does not merge, deploy, enable, or charge any Wave5 provider branch.

The machine-readable source of truth is [`movie-wave5-integration-matrix.json`](./movie-wave5-integration-matrix.json).

## 1. Base and source-ref decision

The checkout was created from the exact production base and verified before changes:

```text
branch: parallel/movie-wave5-integration-prep
HEAD before changes: d87ee7fc60c92d4b6a64d37762cdb21418652e07
origin/main:         d87ee7fc60c92d4b6a64d37762cdb21418652e07
```

The available Wave5 refs are **mapped only**. They are not treated as dependencies of this preparation branch and none is cherry-picked or merged:

| Workstream | Fixed source SHA | Integration risk |
|---|---|---|
| Cost guardrails | `3ad710a326122772555276f1a5f620e45bb8ac87` | Shared usage/cost services and generation preflight; must not turn estimates into charges. |
| Generation QC | `4c57bef1682659e49c76985be1fb314332c05e7a` | Output validation and failure-finalization overlap the movie worker and Asset publication boundary. |
| Image provider hardening | `ad93d342e45fb356885e9bd5684735ef768271d0` | Shared media execution/storage code; movie acceptance must not inherit provider enablement. |
| Media security | `036121eb0f1db9ed9e4410c24212b5e1aa73d416` | Generated-media download, URL, file, and publication checks can conflict with Wave4 storage paths. |
| Movie provider | `f6ed0fc8eb1e51ce06760e02df76771743f52018` | Provider adapter registration/configuration; committed defaults must remain disabled. |
| Music provider | `cb84f23ed0294bf9dfcd6de06c17ea5566ceb645` | Audio provider registration and execution; Wave4 fake/test audio remains the only acceptance path. |
| Provider admin ops | `b9206bf9f5f8bea6d9007d8b4c4db247b0a85d25` | Admin-only health/config surfaces; no provider internals may reach normal movie DTOs. |
| Provider resilience | `13b6ca788e8b53af14441d53a4e12831cbbab981` | Worker claims, retries, circuits, and attempt telemetry; preserve existing job lease/finalization fences. |
| Provider testing | `0394df8535d59cdb8f7f70c9d2d61d243786c70e` | Test adapters and conformance fixtures; adapters must remain test-host scoped. |
| Voice provider | `53123a7b26b9d72743875ddbe19b1ff11956ca0f` | Audio provider registration/configuration; no real voice call in the Last Seed gate. |
| Provider-generation integration candidate | `ee2dabe9f5ca28462d2ab094938f03fcb396399e` | Candidate is based on a different integration history and is not a safe substitute for this exact base. |

No audited source ref implements the Wave5 Movie Studio **segment salvage, minimal insert, or production-intelligence persistence** contracts. This branch therefore adds a migration-free, provider-neutral seam and acceptance vectors rather than inventing production endpoints or integrating an unreviewed candidate.

## 2. Conflict hotspots and resolution rules

### Critical shared files

| Hotspot | Likely owners | Why it conflicts | Resolution rule |
|---|---|---|---|
| `apps/api/Program.cs` | provider branches, current Wave4 | DI registrations for providers, workers, resilience, cost controls, movie services | Compose one registration graph. Test adapters enter only through test-host replacement. Keep normal providers disabled. |
| `apps/api/appsettings.json` and `appsettings.Production.json` | all provider branches | Provider keys, enablement, model fields, billing settings | Preserve `MovieVideo.Enabled=false` and `Billing.CustomerChargingEnabled=false`; never use a provider branch’s defaults as release authorization. |
| `apps/api/Generation/GenerationJobExecution.cs` | QC, media security, resilience, provider branches | Exception mapping, retries, output validation, usage finalization | Retain one authoritative worker lifecycle: claim → attempt → validate → publish → ledger finalization. All failure/QC paths publish no partial Asset and charge zero. |
| `apps/api/Resilience/ProviderResilience.cs` and `EfProviderResilienceStore.cs` | provider resilience/admin/testing | Attempt and circuit telemetry can overlap worker fences | Keep `GenerationJob.ConcurrencyToken` and existing finalization claims authoritative; provider telemetry is additive and sanitized. |
| `apps/api/Assets/GeneratedAssetPublisher.cs`, `apps/api/Files/*` | media security, QC, provider branches | Output validation and private storage publication are common to all media | Preserve private storage, workspace/project ownership, file readiness, and no-publication-on-failure checks. |
| `apps/api/Usage/*` | cost guardrails, provider branches, existing accounting | Estimates, provider cost, customer charge, refunds, budgets share names and paths | An estimate is not a charge. Customer charging remains off. Failed/cancelled/QC-rejected jobs must have `ChargedAmount=0`. |
| `apps/api/Persistence/TaslimDbContext.cs` and `TaslimDbContextModelSnapshot.cs` | any future persisted Wave5 intelligence branch | Existing Movie graph and provider tables are high-conflict EF surfaces | No migration on this prep branch. Future segment/insert/budget entities must be composed once, with one regenerated snapshot and fresh/upgrade PostgreSQL checks. |

### Current Movie graph hotspots

| Hotspot | Existing Wave4 owner | Wave5 integration rule |
|---|---|---|
| `MovieEntities.cs`, `MovieV2Contracts.cs` | project → scene → shot → production version → take | Keep references, raw takes, selected/final pointers, and provenance as the canonical graph; do not create a parallel “intelligence shot” hierarchy. |
| `MovieProductionReferences.cs` | bounded Production Kit | Future segment/insert decisions consume the package hash and continuity provenance; they do not copy mutable Guide/Cast/World fields into a second source system. |
| `MovieShotExecution.cs` | approved-keyframe execution and take batches | Take count remains bounded/idempotent. Segment selection must reference persisted take IDs and never implicitly select a take. |
| `MovieStudioService.cs` and `MovieSelectiveRegeneration.cs` | selective regeneration and cost preview | Salvage evaluation must happen before this route is confirmed. A minimal insert must not silently become whole-shot regeneration. Confirmation and cost guardrails remain explicit. |
| `MovieTakeUpscaleEligibility.cs` and `MovieFinalMastering.cs` | selected-only upgrade/master hand-off | Re-check selected/final status server-side at execution time. Alternate takes stay blocked. No master output is claimed before validated provider output/QC. |
| `MovieCharacterContinuity.cs`, `MovieWorldContinuity.cs`, `MovieProductionContinuity.cs` | character/world snapshots and review | Screen direction is an additive continuity assertion over existing snapshots, not a new canon. Conflicts fail closed with an explainable finding. |
| `MovieTimelineService.cs`, `MovieTimelineTransitions.cs`, `MovieFinalAssembly.cs` | canonical timeline and export | Salvaged segments and inserts must resolve to canonical timeline items. Director recommendations never mutate the timeline without an explicit user override. Export remains a provider-neutral hand-off in this gate. |
| `MovieWave4ExportSeam.cs` | migration-free final hand-off | Preserve deterministic ordering, provenance hash, selected-final equality, and no export Asset/job/charge in the acceptance path. |

## 3. Required integration order after feature branches are approved

1. Rebase or verify every feature branch against the exact final Wave4-combined base; do not use the provider-generation candidate as a base shortcut.
2. Land shared provider/QC/media-security/resilience/cost changes in a serialized pass, reconciling `Program.cs`, configuration, worker execution, usage, and storage first.
3. Re-run provider conformance and disabled-provider safety tests before touching Movie Studio intelligence.
4. Reconcile Movie reference package and shot execution contracts. Keep the Production Kit hash and approved keyframe as the hand-off identity.
5. Add segment selection/salvage/insert entities only if the product decision requires durable review history. If persisted, generate one additive migration after the full model is composed.
6. Map continuity and screen-direction review to existing Character/World snapshots and provenance; do not duplicate locks or canon.
7. Map budget optimization to existing resolution planning/cost guardrails. Keep draft source selection separate from selected-take upgrade/mastering and keep unknown cost explicit.
8. Reconcile browser/API DTOs and add the Last Seed journey only after the final route/DTO shapes are stable.
9. Run fresh and base-to-final PostgreSQL migrations, complete API/frontend gates, and the dedicated Last Seed test with provider-off safety settings. No deployment is part of this task.

## 4. Acceptance seams delivered here

`MovieWave5IntegrationContract` is intentionally pure and migration-free:

- `EvaluatePreflight` requires reference hash, locked references, shot readiness, provider readiness, and budget allowance.
- `BuildSalvagePlan` selects usable ranges first and produces only uncovered minimal inserts.
- `CheckContinuity` verifies a shared continuity anchor and screen direction.
- `OptimizeDraftFirst` records economical draft-first intent, selected-only upgrade, and unknown cost without provider selection or charging.
- `ProvenanceHash` provides a stable boundary for future persisted decision records.

`MovieWave5IntegrationE2ETests` uses the real Wave2–4 HTTP, persistence, job, fake adapter, accounting, timeline, and export seams for everything already implemented. It uses the pure seam only for the not-yet-persisted Wave5 segment/insert intelligence, so a future branch has a clear replacement point rather than a guessed API.

## 5. Safety and compatibility notes

- **No destructive migrations:** none added or changed.
- **Backward compatibility:** no existing route or DTO was removed or changed; the only production-code addition is a pure additive contract with no DI registration.
- **Provider neutrality:** no provider/model/prompt identifiers are in the new normal-user seam or acceptance assertions.
- **Test isolation:** fake Movie/Voice/Music adapters are injected by the existing test host only. The safety fixture uses the production unavailable Movie provider path.
- **Charging:** all acceptance paths assert zero customer charge; the provider-off path asserts failed usage, no output Asset, and no `GenerationJobOutput`.
- **Deployment:** not performed.
- **Merge conflicts:** this branch intentionally leaves all source workstream refs unmerged; the matrix and fixed source SHAs are the integration inputs for the final integrator.
