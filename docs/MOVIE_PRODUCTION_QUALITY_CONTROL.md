# Movie Production Quality Control

Wave 3 adds a provider-neutral, deterministic quality-control seam for movie production outputs. It evaluates measurable evidence and returns one of four internal workflow actions:

- `accept`
- `upscale`
- `regenerate_higher_source_resolution`
- `require_review`

The contract does **not** call a video or upscaling provider, select a model, charge a customer, or make an artistic judgment without evidence.

## Contract

`MovieProductionQcRequest` contains:

- `MovieProductionQcRequirements`: target and minimum source resolutions, expected duration and tolerance, maximum upscale factor, aspect-ratio tolerance, continuity evidence requirements, warning budget, and a recommended higher source resolution.
- `MovieProductionQcEvidence`: current measured resolution, duration, continuity snapshot hash, deterministic continuity warning/conflict counts, and an optional output hash.
- `MovieProductionQcEconomics`: optional additional-cost ceiling and internal estimates for upscale/regeneration. These are estimates only and do not reserve funds or create a usage transaction.

`MovieProductionQcDecision` includes the versioned contract identifier, action, review flag, bounded findings, current/target resolution, recommended source resolution, and the estimated additional cost when known. It contains no provider, model, prompt, credential, or upstream payload fields.

`MovieProductionQcEvidenceParser` reads only provider-neutral measurements from bounded metadata fields. Missing or malformed evidence remains missing; the evaluator never fills in a guessed resolution, duration, or continuity state.

## Deterministic policy

1. Missing/invalid resolution or duration evidence requires review.
2. A duration outside the configured tolerance requires review. Resolution escalation cannot fix a duration mismatch.
3. Aspect-ratio mismatch requires review because upscaling does not repair composition geometry.
4. Missing continuity evidence, a continuity snapshot hash mismatch, any hard continuity conflict, or warning counts above the configured limit requires review.
5. A current resolution at or above the target is accepted when the other checks pass.
6. A lower resolution is eligible for upscaling only when both dimensions meet the minimum source resolution and the required scale is within the configured maximum factor.
7. Otherwise the decision is regeneration at a higher source resolution, with a bounded recommended source resolution.
8. By default, an escalation with unknown additional cost is routed to review. A known estimate above the configured additional-cost ceiling is also routed to review.

Findings are ordered by evaluation path, capped by `MovieProductionQualityControlOptions.MaxFindings`, and use stable internal reason codes such as `MOVIE_QC_CONTINUITY_HASH_MISMATCH` and `MOVIE_QC_SOURCE_RESOLUTION_INSUFFICIENT`.

## Integration seam and safety

The service is registered as `IMovieProductionQualityControl` and is intentionally not invoked by the existing movie provider handler in this batch. Existing providers do not yet guarantee that width/height and continuity measurements are available in every output, and automatically rejecting such outputs would be an unsafe behavior change. A future consumer should:

1. Obtain measurements from validated media inspection and persisted continuity snapshots.
2. Build the QC request from the approved shot/project requirements.
3. Persist the decision and evidence in the existing production version or stage-transition metadata.
4. Require an explicit workflow command before queueing an upscale or regeneration job.
5. Reuse existing generation-job cost guardrails, provider resilience, private storage, and usage accounting; do not create a second provider or billing path.

No database migration, provider activation, charging change, deployment, or normal-user provider/model disclosure is part of this seam.
