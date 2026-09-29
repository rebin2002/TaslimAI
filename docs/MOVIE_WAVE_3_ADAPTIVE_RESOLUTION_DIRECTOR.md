# Movie Wave 3 — Adaptive Resolution Director

## Purpose

This change adds a pure, provider-neutral Adaptive Resolution Director for movie shot planning. It turns shot intent and quality constraints into a deterministic source-resolution and mastering plan without invoking a video service, benchmarking a vendor, estimating a live price, creating a generation job, or changing charging behavior.

The implementation is in `apps/api/Movies/AdaptiveResolutionDirectorWave3.cs`, with focused unit coverage in `apps/api.Tests/AdaptiveResolutionDirectorWave3Tests.cs`.

## Compatibility seam

The Wave-2 resolution contract is not present on `origin/main` at the time this branch was created. To keep this branch independently mergeable, Wave 3 uses an isolated `Taslim.Api.Movies.AdaptiveResolution` namespace and `Wave3`-suffixed contract types. A later integration can map the Wave-2 request/response records into this seam or replace the seam with the shared contract; no Wave-2 branch is merged here.

The seam intentionally uses the same observable concepts as the Wave-2 contract:

- source and master resolution expressed as image-height intents;
- `480p`, `720p`, `1080p`, `1440p`, and `2160p` support;
- native, upscale, and high-quality-mastering pipeline paths;
- quality dimensions for sharpness, temporal stability, detail fidelity, faces, anatomy, continuity, text legibility, lip-sync, and composition;
- deterministic quality confidence, alternatives, rationale, and QC escalation;
- cost preferences recorded as unevaluated constraints until a future economics catalog is available.

`2k` and `4k` are accepted as input aliases and normalized to `1440p` and `2160p`. The output remains height-based so aspect ratio stays in the movie shot contract.

## Inputs

`AdaptiveResolutionDirectorRequest` includes:

- importance and duration;
- motion and camera complexity;
- face, hands/body, fine-detail, continuity, environment, and VFX sensitivity;
- text/signage and lip-sync dependency;
- upscale suitability;
- required quality dimensions;
- speed preference;
- cost constraints and whether quality escalation is allowed.

All scores are inclusive `0..100`. Duration is bounded to `1..3,600` seconds. Required dimensions are bounded and unique, with stable validation errors returned in input order.

## Deterministic planning policy

1. Calculate a bounded demand score from the shot fields, duration pressure, and the strongest required quality dimension.
2. Select a target-aware source baseline, then enforce the quality-tier source floor:
   - Fast → `480p`;
   - Standard → `720p`;
   - Cinematic → `1080p`;
   - Studio → `1440p`.
3. Honor speed or lower-cost preference only when lowering one resolution step still satisfies the tier confidence floor.
4. Use native processing when source and target match. For a demanding sub-`2160p` source mastered to `2160p`, use the high-quality-mastering path; otherwise use upscale.
5. Calculate deterministic confidence from source coverage, demand, upscale suitability, and pipeline path. A confidence value below the tier requirement sets `QcEscalationRequired` and points to the next source when escalation is allowed.
6. Return bounded lower/higher alternatives with explicit trade-offs.

The output is validated before it leaves the director, preventing contradictory source/target/path combinations.

## Economics and provider boundary

`AdaptiveResolutionWave3Constraints` accepts a user cost preference and optional user-supplied maximum estimate, but the director never evaluates money. The output marks `CostEvaluation.Evaluated` as `false` and records `cost_data_unavailable` when a cost constraint was supplied. A future economics layer may consume this recommendation and return a separate capability/cost decision behind the same provider-neutral boundary.

No provider, model, credential, prompt, provider task identifier, or vendor price is present in the request or recommendation contracts.

## Safety status

- No provider registration or provider call was added.
- No generation job is created.
- No database tables or migrations are required.
- No charging or payment behavior changes.
- No normal-user UI fields expose routing identifiers.

## Verification

Focused tests:

```text
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj \\
  --filter FullyQualifiedName~AdaptiveResolutionDirectorWave3Tests
```

The focused run passed all 12 tests.
