# Taslim AI — Movie Wave 3 Conflict Map

**Fixed comparison base:** `origin/main` at `308e146aa73e91443097229a81f97dac008b92ee`
**Integration branch:** `integration/movie-production-intelligence-wave3`
**Rule:** inspect and integrate fixed SHAs; do not merge moving branch tips or unrelated work into the integration branch.

## Highest-risk hotspots

| Hotspot | Wave 3 refs | Risk | Required resolution |
|---|---|---|---|
| `apps/api/Movies/MovieEntities.cs` / `MovieV2Entities.cs` | adaptive resolution, selected-take upscale, final mastering, video adapter | Shared MovieShot/MovieTake/clip relationships can compile while selection or source ownership semantics drift | Preserve one canonical `SelectedTakeId`/`FinalTakeId` authority; add nullable audit/master links only after Wave 2 model reconciliation |
| `apps/api/Movies/MovieStudioService.cs` | resolution pipeline, selected-take upscale, final mastering, shot intelligence | Projection and mutation paths overlap around production versions, takes, costs, and provenance | Keep one authorization path, one production-version lifecycle, and explicit hand-offs; no direct provider call from a read or eligibility endpoint |
| `apps/api/Controllers/MovieStudioController.cs` / `MovieStudioV2Controller.cs` | upscaling, selected-take upscale, final mastering, resolution pipeline | Route collisions and inconsistent permission/CSRF behavior | Keep routes additive, product-facing, CSRF-protected for mutations, and use stable domain error codes |
| `apps/api/Program.cs` | benchmark framework, capability registry, video adapter, provider health/fallback, resolution pipeline, upscaling | DI registration order can silently replace the unavailable provider or add a worker twice | Compare the complete service graph; normal defaults must resolve to unavailable provider and no paid executor |
| `apps/api/Persistence/TaslimDbContext.cs` | benchmark framework, upscaling pipeline, selected-take audit, final mastering, provider health | Entity sets, indexes, delete behavior, and ownership can diverge | Compose all entities once; inspect FK ownership and delete behavior; run EF model check and fresh/upgrade PostgreSQL |
| `TaslimDbContextModelSnapshot.cs` | all migration-bearing refs | Textual snapshot merges can pass compile while omitting columns/indexes | Regenerate from the composed model; never hand-edit snapshot conflict markers into a plausible state |
| `apps/api/Domain/Entities.cs` and usage ledger | benchmark/economics, provider adapters, final mastering | Estimates, actual cost, and customer charge can be conflated | Preserve `EstimatedProviderCostUsd`, actual provider cost, and `ChargedAmount` as separate fields; failed/QC-rejected jobs charge zero |
| `apps/api/Generation/GenerationJobExecution.cs` | provider adapter, health/fallback, upscaling/final mastering executors | A future executor could publish before QC or finalize usage on failure | Reuse the existing prepare/validate/publish/finalize sequence and add negative-path tests first |
| `apps/api.Tests/ProviderGenerationE2ETests.cs` and movie operational harness | existing provider-safe E2E plus Wave 3 branches | Fake adapters can make production readiness look enabled | Keep fake registration test-host-only; assert production configuration and readiness false in a separate safety fixture |
| `apps/web/e2e/*` and Movie workspace read models | Wave 3 UI consumers | User UI may expose provider/model names or render unpersisted upscale/master output | Add selectors only for product concepts (resolution, selected take, QC, estimate); assert no vendor identifiers/prompts and no fake output |

## Migration collision order

The audited Wave 3 refs include these migration-bearing changes:

1. Reconcile the missing Wave 2 migrations first: shot production contract, complexity, and importance, in the final selected integration order.
2. `20260929213039_AddProviderBenchmarkingFoundation`.
3. `20260929213223_AddUpscalingJobs`.
4. `20260929213333_AddMovieTakeUpscaleAudit`.
5. `20260929213335_AddMovieFinalMastering`.

Timestamp order is a useful default, not authority. If the composed model requires a different order, record the reason and regenerate the final snapshot. Validate both a fresh database and an upgrade database populated through the base schema; inspect `__EFMigrationsHistory`, indexes, FK constraints, and nullable behavior.

The integrated final-mastering migration is reconciled to own only `MovieFinalMasters`; continuity, shot-planning, and selective-regeneration schema remain owned by their earlier migrations. This avoids duplicate-column/table operations on fresh databases while preserving the composed model snapshot.

## Semantic conflicts to resolve explicitly

- **Resolution vs. quality:** adaptive recommendation may suggest a source/master plan, but quality-tier and QC contracts remain authoritative. Do not turn a complexity score into a vendor/model choice.
- **Economical draft vs. final quality:** a draft may use a lower source resolution and remain clearly a draft. It must not become eligible for premium mastering until its take is explicitly selected/finalized and ready.
- **Benchmark vs. routing:** measurement aggregates are observations. A benchmark result must not silently rewrite a shot, select a provider, or expose internal identifiers in a normal-user DTO.
- **Estimate vs. charge:** a cost estimate is informational and auditable; it is not a customer charge. Unknown cost must remain unknown rather than zero by assumption.
- **Selection vs. execution:** selection/finalization belongs to Movie V2 server state. Upscale/mastering eligibility must re-check that pointer immediately before execution and must not enumerate every candidate take.
- **QC vs. success:** a QC escalation or rejection cannot produce a successful output Asset. A retry or escalation must preserve source/provenance and keep failed usage non-chargeable.
- **Provider OFF vs. test adapter:** deterministic test adapters are allowed only in test hosts. They must not alter appsettings defaults, production DI behavior, or normal-user readiness responses.

## Conflict resolution checklist

- [x] Fixed SHAs reviewed and integrated from the verified base.
- [x] Wave 2 dependency contract landed in the verified base and Wave 3 fields reconciled without guessed provider fields.
- [x] One final `MovieShot`/`MovieTake` selection authority preserved.
- [x] One final EF snapshot validated against the composed model.
- [x] Fresh and upgrade PostgreSQL migration checks pass.
- [x] Failed, cancelled, unavailable, and QC-rejected jobs publish no Asset and charge zero.
- [x] Reload returns selected/final pointers and source/provenance links.
- [x] Normal-user Movie DTO/UI contains no provider/model/prompt/credential details; provider-boundary and admin accounting metadata remain internal.
- [x] Movie video and customer charging remain OFF for the acceptance gate.
- [x] No conflict markers, secrets, deployment, or paid generation activity.
