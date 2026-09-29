# TASLIM AI — Movie Wave 2 Integration Preparation

**Prepared branch:** `parallel/movie-wave2-integration-prep`
**Authoritative base:** `308e146aa73e91443097229a81f97dac008b92ee` (`main`)
**Scope:** integration framework, regression coverage, merge planning, and future release-gate preparation only.

> The requested branch did not exist on the remote at audit time. This branch was created locally from the exact supplied base. No Wave 2 branch was merged, no deployment was performed, no provider was enabled, and customer charging remains disabled.

The machine-readable source of truth is [`docs/movie-wave2-integration-matrix.json`](./movie-wave2-integration-matrix.json). This document explains the decisions behind it.

## 1. Audited Wave 2 refs

All five available Wave 2 refs were single-commit descendants of `main` at audit time. They were inspected with `git diff` and `git show`; they were not merged or cherry-picked.

| Branch | SHA | Migration | Changed-file count | Integration note |
|---|---|---:|---:|---|
| `parallel/movie-wave2-scene-planning` | `0837804a4f30f8c134269da428f828ba6df4f265` | No | 11 | Director scene-plan action, validation, executor, and proposal flow |
| `parallel/movie-wave2-shot-contract` | `cfd880c7d1ae2401a06b794d6c6bd886761728a9` | Yes — `20260929132818_AddMovieShotProductionContract` | 12 | Additive nullable shot contract fields plus EF snapshot/migration |
| `parallel/movie-wave2-complexity` | `3b9c281f1c3d3101d2de4cbf8886620a21bd1ba0` | Yes — `20260929133106_AddMovieProductionComplexity` | 14 | Bounded per-shot complexity assessment and Director context projection |
| `parallel/movie-wave2-shot-importance` | `1d5b728ef2674f30ecfd411edcf1f544ac226031` | Yes — `20260929133111_AddMovieShotImportance` | 10 | Deterministic Director classification plus user override |
| `parallel/movie-wave2-duration-budget` | `1987df8d8712511a6c4517c3a31800fefb466e9c` | No | 7 | Runtime budget validator and write-path duration validation |

The remote did not expose `parallel/movie-wave2-integration-prep`; the branch is intentionally a new preparation branch rather than an assertion that an upstream branch existed.

## 2. Integration matrix summary

| Surface | Base status | Expected contract at integration | Primary verification |
|---|---|---|---|
| Scene planning | Partial | Director proposal carries a bounded, grounded scene plan; approval alone does not persist scenes; explicit execution does | `MovieDirectorScenePlanningTests`, validation tests, Last Seed |
| Shot planning | Implemented foundation | Explainable readiness, ordering, archiving, duration coverage; planning never queues generation | `MovieShotPlanningApiTests`, frontend Scenes/Shot tests |
| Shot production contract | Partial | Versioned, provider-neutral `productionContract`; nullable additive persistence; no second approval/take/master model | `MovieShotProductionContractTests`, EF fresh/upgrade |
| Complexity profile | Not on base | Eleven bounded 0–100 dimensions, deterministic aggregate/reasons, validated versioned snapshot; no resolution/provider/cost choice | complexity unit/API tests, context tests, EF gate |
| Shot importance | Not on base | Director classification plus explicit user override, bounded evidence, project authorization | importance tests, authorization tests |
| Quality requirements | Partial | Existing `Fast`/`Standard`/`Cinematic`/`Studio`; shot-level acceptance criteria remain provider-neutral | production workflow and contract tests |
| Runtime budgeting | Partial | Deterministic integer-second scene/shot totals, bounded tolerance, explicit incomplete/invalid diagnostics, no auto-scaling | duration tests, 30-second regression |
| Continuity | Implemented foundation | Guide, character, world, and shot facts/locks remain source authority; context uses bounded snapshots/hashes | character/world/context/operational tests |
| Cinematography | Implemented foundation | Structured intent and explicit capability classification; presets do not assert provider-native support | cinematography and contract tests |
| Scenes UX | Implemented foundation | Real persisted scenes, honest empty states, visible approval/apply boundary, Quick Movie separation | component tests, operational browser suite |
| Shot UX | Implemented foundation | Readiness/duration/cinematography visible; planning separate from production; no fake outputs | component/API/operational tests |
| Director room awareness | Implemented for existing rooms | Project/Scene/Shot/Cast/World target context is bounded, hashed, and approval-gated | Director context and Last Seed Cast-room checks |
| Story → Scene context | Implemented foundation | Approved Story is authoritative; screenplay scenes map to canonical MovieScene; stale source is rejected | Story/scene tests, Last Seed |
| Guide/Cast/World production context | Implemented foundation | Locked Guide is the hand-off; Cast/World stay reusable source systems; cross-boundary refs fail | Guide/Cast/World/context tests |
| Security/approval | Implemented foundation | CSRF, workspace + Movie permission, explicit proposal approval, explicit execution; reviewer does not gain Generate/Budget | authorization/collaboration/production tests |
| Output validation | Implemented foundation | Malformed, ungrounded, unsafe, or QC-rejected output fails safely with no partial publication | creative validation/QC/provider-safe tests |
| Placeholder audit | Implemented acceptance coverage | New Full Movie has no fake scenes/shots/cast/world/clips/assemblies/assets; unavailable states stay honest | Last Seed and operational tests |
| Last Seed acceptance | Wave 1 acceptance exists | Preserve exact title/brief/settings and extend only after merged contracts exist; assert zero charge and disabled provider | dedicated Playwright test + full suite |
| Adaptive Resolution contract | Future contract only | Complexity/quality/cinematography/runtime are inputs only; no persistence-level resolution/provider/cost decision is invented | contract tests when an approved implementation exists |

The complete machine-readable rows, dependencies, and commands are in [`movie-wave2-integration-matrix.json`](./movie-wave2-integration-matrix.json).

## 3. Recommended dependency and merge order

This is a **conflict-aware recommendation**, not an instruction to merge incomplete branches.

### Stage 0 — Preparation branch first

1. Start from `main` at `308e146aa73e91443097229a81f97dac008b92ee`.
2. Land this preparation branch or equivalent gate artifacts first.
3. Keep all incoming branches available as reviewable refs. Do not use a moving branch tip as a substitute for a reviewed SHA.

### Stage 1 — Domain and context shape

4. **Scene planning** first among the five refs. It owns the new Director scene-plan action/executor and has the strongest dependency on approved Story, Guide, Cast/World, continuity, and runtime context. It also changes shared Director files that complexity will touch.
5. **Shot production contract** next. It establishes the stable provider-neutral shape and additive shot persistence that complexity, importance, quality, and future adaptive-resolution work must consume. Apply and validate its migration before any dependent migration.

### Stage 2 — Independent shot assessments, serialized at conflict boundaries

6. **Complexity profile** after the shot contract. It adds a separate assessment history and projects the latest profile into Director context; it must not reinterpret the shot contract as a provider decision.
7. **Shot importance** after the shot contract and after complexity if that is the order selected by the integrator. Complexity and importance are conceptually independent, but both edit `MovieEntities.cs`, EF model wiring, and `Program.cs`; serializing them avoids a false “independent” merge.
8. **Duration budget** after the scene/shot model is stable. It changes `MovieStudioController`, `MovieStudioService`, and `MovieShotPlanningApiTests`; the latter also belongs to the shot-contract integration surface. Running it last makes the runtime gate observe the final active hierarchy and avoids silently dropping shot-contract request/response assertions.

### Stage 3 — Consumer UX and acceptance

9. Reconcile Scenes UX and Shot UX against the final DTOs. Existing `FullMovieWorkspaceView` is a baseline consumer, but new optional members must render as “unspecified” rather than inventing values.
10. Reconcile Director room awareness and Story → Scene context against the final context snapshot/hash and proposal payloads.
11. Extend the exact Last Seed flow only after API contracts, migrations, and UX selectors are stable. Keep the acceptance provider-safe: test-only deterministic adapters may be injected by test hosts, but production config must remain disabled.
12. Run the complete validation gate, including a disposable PostgreSQL fresh migration and a real upgrade from the base schema. Do not deploy from a branch that has only passed SQLite `EnsureCreated()` tests.

### Migration ordering rule

The three additive migrations must be applied in the order selected by the final integration history, not by timestamp alone. If the integrator merges in the recommended order, the expected logical sequence is:

1. `AddMovieShotProductionContract`
2. `AddMovieProductionComplexity`
3. `AddMovieShotImportance`

The final EF snapshot must be regenerated/reconciled once after the composed model is final. `dotnet ef migrations has-pending-model-changes` must be clean. A fresh PostgreSQL database and a PostgreSQL upgrade database must both be exercised. Do not hand-edit migration history to hide a model mismatch.

## 4. Conflict hotspots

Pairwise changed-file overlap was observed before any merge:

| Hotspot | Overlapping refs | Risk | Resolution guidance |
|---|---|---|---|
| `apps/api/Movies/MovieEntities.cs` | shot-contract, complexity, shot-importance | Same `MovieShot` model receives additive fields and/or assessment relationships | Merge contract fields first; compose later properties deliberately; preserve nullable/no-backfill semantics |
| `apps/api/Movies/MovieStudioService.cs` | shot-contract, complexity, duration-budget | DTO projections, writes, and validation paths overlap | Reconcile from the final domain contract; retain one authorization and one validation path |
| `apps/api/Movies/MovieDirectorServices.cs` | scene-planning, complexity | Context assembly and proposal consumers overlap | Keep one bounded context assembler; add projections without duplicating or bypassing snapshot/hash checks |
| `apps/api/Movies/MovieDirectorEntities.cs` | scene-planning, complexity | Director payload/entity additions can collide | Preserve existing proposal/action approval lifecycle and provider-neutral payloads |
| `apps/api/Controllers/MovieStudioController.cs` | complexity, shot-importance, duration-budget | Multiple new endpoints and error mappings | Keep routes additive, CSRF-protected, project-authorized, and use stable error codes |
| `apps/api/Program.cs` | scene-planning, complexity, shot-importance, duration-budget | DI registrations and options may be dropped by a textual merge | Compare the complete registration graph after merge; do not add a second provider/router/worker |
| `apps/api/Persistence/TaslimDbContext.cs` | shot-contract, complexity, shot-importance | Entity sets, relationships, indexes, delete behavior | Compose all model configuration and run EF model check plus fresh/upgrade DB tests |
| `TaslimDbContextModelSnapshot.cs` | shot-contract, complexity, shot-importance | Snapshot conflicts can compile while producing a wrong schema | Regenerate from the composed model; inspect generated SQL/schema and migration history |
| `apps/api.Tests/MovieShotPlanningApiTests.cs` | shot-contract, duration-budget | Existing planning test gets request/response contract changes | Preserve existing readiness/no-generation assertions and add duration/contract assertions rather than replacing them |
| `FullMovieWorkspaceView.tsx` and Movie CSS | future UX branches, current baseline | DTO shape and empty-state regressions | Use semantic selectors and real API-backed fixtures; keep no-placeholder language and Quick/Full separation |

A conflict marker audit is mandatory after every integration conflict resolution; it is not enough that the code compiles.

## 5. Future integration validation gate

The gate is codified in [`scripts/movie-wave2-integration-gate.sh`](../scripts/movie-wave2-integration-gate.sh). It runs safe local checks by default and enables destructive-or-service-dependent checks only when explicit disposable-environment variables are provided.

### Required checks

- **API:** build and complete `dotnet test` suite; focused Movie/Director/authorization/quality/output suites.
- **Frontend:** Vitest, TypeScript (`tsc --noEmit`), ESLint, and production Next build.
- **Browser:** full Playwright suite plus the dedicated `last-seed-acceptance.spec.ts` Chromium run.
- **Database:** EF pending-model check; fresh migration on an empty disposable PostgreSQL database; upgrade migration from a base-schema disposable PostgreSQL database; inspect `__EFMigrationsHistory` and expected schema.
- **Authorization:** workspace/project isolation, collaboration permission matrix, CSRF, approval-before-execution, reviewer authority boundaries.
- **Creative fallback audit:** no production deterministic creative fallback; invalid/unavailable creative output fails safely; test-only deterministic responses remain test-host scoped.
- **Output validation:** no malformed/ungrounded output reaches persistence or Asset publication; no partial Asset on failure.
- **Repository hygiene:** `git diff --check`, conflict-marker audit, no committed secrets.
- **Safety invariants:** `Billing:CustomerChargingEnabled` remains `false`; `MovieVideo:Enabled` remains `false` in normal app configuration; no paid provider call is made by Last Seed. Test-only fake provider injection is allowed only inside test hosts and must not change committed production defaults.

### Safe control variables

```text
FRESH_DATABASE_URL=<empty disposable PostgreSQL database>
UPGRADE_DATABASE_URL=<disposable PostgreSQL database pre-populated through the base migration>
RUN_BROWSER_E2E=1
RUN_DB_MIGRATIONS=1
```

The gate never drops a database and never points at production. The integrator is responsible for provisioning isolated disposable databases and for recording the migration-history/schema results.

## 6. Regression scaffolding added on this branch

- `docs/movie-wave2-integration-matrix.json` — machine-readable 19-surface matrix, source refs, dependencies, gate commands, and safety defaults.
- `docs/MOVIE_WAVE_2_INTEGRATION_PREP.md` — integration expectations, order rationale, conflicts, and risks.
- `scripts/movie-wave2-integration-gate.sh` — repeatable safe gate runner with opt-in browser/database stages and static charging/provider invariants.
- `apps/api.Tests/MovieWave2IntegrationPrepTests.cs` — provider-neutral baseline contract sentinels using only existing APIs; protects quality levels, production-stage hand-offs, shot-readiness/generation separation, and explicit approval semantics while Wave 2 branches are composed.

No new production endpoint, provider, billing behavior, migration, or fake production data is added by this preparation branch.

## 7. Known risks and explicit non-goals

1. **The requested preparation branch was absent upstream.** This branch must be pushed as a new ref; its base is explicit and no ancestry is invented.
2. **The incoming branches are not mutually based.** Their single-commit tips should be reviewed at fixed SHAs; do not assume a later branch contains another branch's work.
3. **EF snapshots are high risk.** Three branches modify the snapshot/model; SQLite `EnsureCreated()` tests do not validate upgrade compatibility.
4. **Scene planning has no migration but has broad Director overlap.** A clean migration diff does not mean a low-conflict merge.
5. **Adaptive Resolution is not implemented in the audited refs.** Do not add a guessed endpoint, resolution policy, provider capability decision, or cost formula to “complete” the matrix.
6. **Complexity and importance are assessments, not authority.** Neither should silently mutate a locked Guide, approved Story, shot contract, or user override.
7. **Current browser coverage is Wave 1/foundation coverage.** New Scenes/Shot controls must be wired to real persisted contracts and then added to the browser journey; a passing list-only Playwright run is not acceptance.
8. **Test-only provider injection can obscure production safety.** The operational harness deliberately enables a fake provider only in its test host. The committed app defaults and Last Seed acceptance must continue to report Movie video unavailable.
9. **Runtime budget is diagnostic unless a later workflow gates on it.** Integration must not silently scale user/model durations or turn a diagnostic into a generation decision.
10. **Charging and deployment are out of scope.** This branch does not deploy, enable Movie video, enable billing, or change provider readiness.
