# Movie Runtime and Duration Budget Engine

## Scope

The duration-budget engine validates the existing hierarchy:

> `MovieProject → MovieScene → MovieShot`

It is provider-neutral and does not select resolution, provider, credits, or generation behavior. It only validates persisted or proposed integer-second durations and returns explainable diagnostics.

## Read contract

`GET /api/movie-studio/projects/{movieProjectId}/duration-budget`

The route requires Movie project view access and returns `MovieDurationBudgetResult`:

- `status`: `Valid`, `UnderBudget`, `OverBudget`, `Incomplete`, or `Invalid`;
- `isValid`: true only when all active durations are present, bounded, and compatible within tolerance;
- `isComplete`: false when a required active scene/shot duration is missing or the active hierarchy is empty;
- `targetDurationSeconds`;
- deterministic `totalSceneDurationSeconds` and `totalShotDurationSeconds`;
- `sceneDeltaSeconds`: total scene runtime minus the movie target;
- `movieToleranceSeconds`;
- per-scene results with declared duration, nested shot total, delta, status, and diagnostics;
- diagnostics with stable `code`, `severity`, `path`, message, and optional expected/actual seconds.

The pure entry point is:

```csharp
MovieDurationBudgetValidator.Validate(MovieDurationBudgetInput input)
```

The input records are safe to use for creative-model proposals before persistence. The validator does not mutate or normalize them, so an accepted user override remains the exact value supplied by the user.

## Deterministic algorithm

1. Validate the movie target is an integer in `1..86,400` seconds.
2. Ignore archived scenes and shots for active runtime totals.
3. For every active scene, require a positive duration and sum its active positive shot durations.
4. Compare each shot total to its parent scene duration.
5. Sum scene durations and compare that total to the movie target.
6. Report `UnderBudget` or `OverBudget` when a difference exceeds tolerance. A difference inside tolerance is `Valid`.
7. Missing values produce `Incomplete`; non-positive, duplicate, or absurd values produce `Invalid` diagnostics. A grossly incompatible total is flagged with an explicit error diagnostic while retaining the `UnderBudget`/`OverBudget` status for UI and planning consumers.

All totals use `long` internally and in the result contract to avoid overflow from large plans. No floating-point arithmetic is used for totals.

## Bounds and tolerance

- Movie target maximum: **86,400 seconds** (24 hours).
- Scene duration maximum: **86,400 seconds**.
- Shot duration maximum: **3,600 seconds**.
- Tolerance: `ceil(duration × 2%)`, bounded to **1..10 seconds**.
- Tolerance is inclusive. For example, a 50-second scene has a 1-second tolerance, so 49 seconds of shots is still within tolerance; 48 seconds is under-budget.
- Gross deviation is a difference greater than `max(10 seconds, target / 2)` and emits `movie_duration_grossly_under_budget` or `movie_duration_grossly_over_budget`.

This keeps a 30-second short-form movie practical while preventing plans such as a 120-second scene from silently being treated as a valid 30-second movie.

## Write behavior

Explicit scene and shot duration values are validated on Movie Studio create/update paths:

- omitted duration remains allowed for drafts and is reported as `Incomplete` by the budget engine;
- zero and negative values are rejected with a clear 400 validation response;
- values beyond the bounded maximum are rejected;
- accepted values are persisted unchanged;
- the budget route is read-only and never queues generation.

## Non-goals and limitations

- No automatic redistribution, scaling, rounding, or repair of user/model durations.
- No creative scene or shot generation.
- No provider, resolution, credit, charging, or generation decisions.
- The engine validates active Movie scenes/shots; clips and takes are not alternate runtime sources.
- Under/over-budget results are planning diagnostics and must be explicitly gated by a later workflow if a caller wants to block approval or generation.
