# Movie Wave 2 — Adaptive Resolution Director Contract

## Purpose

This document defines the provider-neutral input/output contract for a future Adaptive Resolution Director. It lets AI Core express a shot's quality requirement and resolution strategy without selecting a vendor, model, credential, price, or provider operation.

This wave is contract-only:

- no real video provider is integrated or enabled;
- no paid generation is performed;
- no charging or pricing catalog is added;
- no API route, database table, or UI mutation is required yet;
- the existing Movie Video provider remains disabled and unavailable by default.

The implementation is in `apps/api/Movies/AdaptiveResolutionDirector.cs` with focused tests in `apps/api.Tests/AdaptiveResolutionDirectorTests.cs`.

## Contract shape

`AdaptiveResolutionRequest` contains:

| Group | Fields | Scale/notes |
|---|---|---|
| Master intent | `TargetMasterResolution`, `ProjectQualityTier` | Master is `1080p`, `2k`, or `4k`; tier reuses Movie Director's `Fast`, `Standard`, `Cinematic`, or `Studio` tiers. |
| Shot demand | `ShotImportance`, `DurationSeconds` | Importance is 0–100; duration is 1–3,600 seconds. |
| Visual/temporal complexity | `MotionComplexity`, `CameraComplexity`, `EnvironmentComplexity`, `VfxComplexity` | 0–100; higher means more demanding. |
| Subject/detail sensitivity | `FaceImportance`, `HandBodyComplexity`, `FineDetailImportance`, `TextSignageSensitivity`, `LipSyncDependency` | 0–100; higher means the dimension is more important to preserve. |
| Continuity and processing | `ContinuitySensitivity`, `UpscaleSuitability` | 0–100; upscale suitability is higher when the shot tolerates upscaling. |
| Required quality | `RequiredQualityDimensions` | Bounded, unique list of dimension names with 0–100 importance. |
| Operational preference | `SpeedPreference` | 0–100; higher favors speed when the quality floor remains satisfied. |
| Cost constraints | `CostConstraints` | Optional lower-cost preference, user-supplied maximum estimate, and whether QC escalation may increase source quality. No vendor price is inferred. |
| User control | `UserOverride` | Optional source resolution, master target, and path override. Valid overrides are retained in the output. |

### Canonical resolution vocabulary

Source resolutions are `480p`, `720p`, `1080p`, `1440p`, and `2160p`. Master intents are `1080p`, `2k` (1440-height intent), and `4k` (2160-height intent). Height is intentional: aspect ratio remains part of the movie shot contract rather than being encoded here.

Supported processing paths are:

- `native` — source matches the master target;
- `upscale` — source is below the master target and is intended to be upscaled;
- `native_high_quality_then_4k_master` — a high-quality sub-4K source is reserved for a 4K mastering step.

### Quality dimensions

The stable vocabulary is:

`sharpness`, `temporal_stability`, `detail_fidelity`, `face_identity`, `anatomy`, `continuity`, `text_legibility`, `lip_sync`, and `composition`.

The vocabulary is deliberately about observable quality outcomes, not provider controls.

## Output contract

`AdaptiveResolutionRecommendation` represents:

- `RecommendedSourceResolution` — the source resolution to request or otherwise obtain;
- `IntendedUpscaleTarget` — always the requested master resolution, including when the path is native;
- `ProcessingPath` — native, upscale, or high-quality source followed by 4K mastering;
- `QualityRequirement` — project tier, required dimensions, and deterministic minimum confidence;
- `QualityConfidence` — deterministic planning confidence in the 0–1 range, not a provider claim;
- `QcEscalationRequired` and `Escalation` — whether predicted confidence is below the tier requirement and, when possible, the next source resolution to try;
- `ReasonCodes` and `Rationale` — stable machine-readable codes plus bounded human-readable explanations;
- `Alternatives` — deterministic lower/higher quality trade-offs;
- `UserOverride` — requested/applied status and the applied intent;
- `CostEvaluation` — explicitly `Evaluated: false` until future capability/cost data is supplied.

The output contains no provider name, model name, provider URL, provider task ID, credential, or vendor price.

## Deterministic validation

`AdaptiveResolutionRequestValidator.Validate` returns ordered validation errors rather than relying on provider behavior. It checks:

1. master resolution and project quality tier vocabulary;
2. duration and every score boundary;
3. required quality dimension count, vocabulary, uniqueness, and importance;
4. non-negative user cost ceilings;
5. override vocabulary, target matching, source/master ordering, and path relationships.

`AdaptiveResolutionDirector.Recommend` refuses invalid input with `AdaptiveResolutionRequestValidationException`. It also validates its own result with `AdaptiveResolutionRecommendationValidator` before returning it. This protects the future provider routing layer from malformed or contradictory plans.

## Baseline planning rules

The current planner is intentionally conservative and deterministic, not a provider benchmark:

1. It computes a bounded weighted demand score from shot importance, motion/camera complexity, face/anatomy/detail sensitivity, continuity, environment, VFX, signage, and lip-sync dependency.
2. It chooses a source floor from the project tier: Fast → 480p, Standard → 720p, Cinematic → 1080p, Studio → 1440p.
3. It selects a target-specific baseline source, then raises it when upscale suitability is low.
4. Speed and lower-cost preferences can lower the source only when the quality floor remains intact.
5. A low-confidence recommendation produces a QC escalation to the next source resolution when one exists. If the target is already the highest available source, escalation remains required but the next source is null, signaling QC review rather than silently claiming success.
6. User overrides are applied after the baseline selection and are always visible in the output.

The formulas and thresholds are implementation details of this deterministic baseline. A future capability-aware engine may replace the selection policy while preserving the request and response contract.

## Future provider capability and cost plug-in

The contract is the boundary between creative intent and provider routing:

```text
Movie shot intent
      |
      v
AdaptiveResolutionRequest
      |
      v
AdaptiveResolutionRecommendation   <-- this wave
      |
      +--> capability catalog matches source/path/target requirements
      |
      +--> cost catalog supplies current route estimates (server-side only)
      |
      v
provider-neutral route decision
      |
      v
provider adapter request (internal; not this contract)
```

A future AI Core capability/cost service should consume the recommendation and evaluate candidate route envelopes such as:

- source resolution supported;
- target/master resolution supported;
- native versus upscale versus mastering path supported;
- expected quality dimensions and confidence adjustment;
- QC/escalation support;
- current estimated cost, currency, pricing version, and budget eligibility.

That catalog belongs behind an internal resolver interface and may contain provider/model keys. Those keys must not be added to `AdaptiveResolutionRequest`, `AdaptiveResolutionRecommendation`, persisted user DTOs, or normal movie responses. The resolver can return the best eligible candidate or a structured “capability unavailable” result, while the provider adapter maps only the selected internal route to vendor-specific fields.

Pricing is intentionally not hardcoded here. `MaxEstimatedCostUsd` is a user constraint only; until a current catalog is supplied, `CostEvaluation` remains unevaluated and records `cost_data_not_available` when a cost preference or ceiling was supplied.

After generation, QC can feed measured dimensions and confidence back into the same requirement. If confidence is below the requirement, the next source resolution in `Escalation` can be requested without changing the creative shot contract.

## Example 1 — fast 1080p social insert

Input and a representative output shape are in [`examples/adaptive-resolution/fast-1080p.json`](../examples/adaptive-resolution/fast-1080p.json). The expected intent is a 480p source with an intended upscale to 1080p, subject to the normal QC gate.

## Example 2 — 4K face/VFX/continuity-sensitive shot

Input and a representative output shape are in [`examples/adaptive-resolution/studio-4k.json`](../examples/adaptive-resolution/studio-4k.json). The expected intent is a 1440p or 2160p source, 4K mastering, explicit face/anatomy/continuity requirements, alternatives, and a possible QC escalation.

## Safety status

This change adds no provider registration and changes no charging behavior. The existing Movie Video configuration remains disabled (`Enabled: false`) and falls back to the unavailable provider. No generation job is created by this contract; the planner is pure and deterministic.
