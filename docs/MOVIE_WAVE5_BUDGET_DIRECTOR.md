# Movie Wave 5 — Budget Director

## Purpose

The Movie Budget Director is a read-only, provider-neutral planning seam for the filmmaking workflow. It compares:

1. a deliberately expensive **naive high-cost path** that renders every shot/candidate pass at a high-finish target; and
2. an **optimized draft/select/master path** that drafts cheaply, reuses approved references, salvages usable ranges, generates targeted inserts, and reserves mastering for selected takes.

The implementation is in `apps/api/Usage/MovieBudgetDirector.cs` and is exposed through:

```text
GET /api/movie-studio/projects/{id}/budget-director
```

An authenticated, CSRF-protected preview can also accept explicit planning counts without persisting them:

```text
POST /api/movie-studio/projects/{id}/budget-director/preview
```

The Movie overview renders the comparison in the Budget Director card. The endpoint only reads the project and persisted capability-pricing catalog. It never queues generation, invokes media providers, changes a take, writes an accounting record, or changes charging behavior.

## Cost calculation

Each workflow component calls the existing `IMovieGenerationCostEstimator`, which remains the server-trusted source of provider-neutral pricing ranges. The director scales a representative shot-duration estimate by the planned number of generation units and combines the ranges.

The optimized path models these explicit planning levers:

- **Low-resolution drafts:** candidate passes use a low-resolution native draft path.
- **Approved reference reuse:** explicitly supplied reuse counts reduce planned draft units without pretending that a reference was approved when it was not recorded.
- **Salvaged selects:** explicitly supplied salvage counts reduce planned regeneration units.
- **Targeted inserts:** missing coverage is estimated as short insert work rather than a full-scene regeneration.
- **Selected-only mastering:** the finish pass is modeled only for selected/final takes by default.

If any required component is unknown or unevaluated, the director fails closed: the scenario remains unavailable and savings are not converted to zero.

## Estimate and accounting semantics

Every returned comparison is labeled as an **estimate**. Savings are returned as a minimum/maximum avoided-cost range only when both paths have complete pricing. `ActualSavingsAvailable` is always false for this preview and `ActualSavingsUsd` is null. Actual savings require completed ledger evidence comparing realized work; the director never infers them from an estimate, a provider catalog, a selected take, or a mastering request.

The normal-user response contains no provider key, model key, prompt, endpoint, credential, or internal pricing provenance. Server-only pricing metadata remains ignored by JSON serialization through the existing cost-estimate contract.

## Compatibility and migrations

- Additive API and frontend surface only.
- No database entities or migrations are required.
- Existing generation, selected-take-only mastering, provenance, recovery, timeline, and usage-ledger contracts are unchanged.
- Provider execution remains disabled by the committed Wave4 safety defaults.
- No customer charging or deployment behavior changed.
