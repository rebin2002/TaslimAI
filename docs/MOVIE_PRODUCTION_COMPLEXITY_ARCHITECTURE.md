# Movie Production Complexity Profile

## Purpose

`MovieProductionComplexityProfile` is a provider-neutral, per-shot assessment for the future Adaptive Resolution Director. It describes production difficulty and sensitivity only. It does **not** select resolution, calculate provider cost, name a video vendor, queue generation, or charge a user.

## Canonical contract

A submitted profile contains these eleven normalized dimensions. Each dimension is an integer in the inclusive range `0..100`:

| Dimension | Meaning |
| --- | --- |
| `motionComplexity` | Difficulty of representing subject or scene motion. |
| `cameraComplexity` | Difficulty of the camera movement, framing changes, or coordinated camera behavior. |
| `faceImportance` | Importance of recognizable, stable, expressive faces. |
| `handBodyInteractionComplexity` | Difficulty of hands, body mechanics, contact, and coordinated interaction. |
| `fineDetailImportance` | Sensitivity to small visual details, texture, or object fidelity. |
| `environmentComplexity` | Complexity of the surrounding set, world, lighting, or environmental state. |
| `vfxComplexity` | Complexity of effects, atmospheric phenomena, or composited elements. |
| `continuitySensitivity` | Cost of visual inconsistency with locked or adjacent shot facts. |
| `textSignageSensitivity` | Importance of legible or exact text, labels, screens, and signage. |
| `dialogueLipSyncDependency` | Dependence on synchronized spoken performance and mouth motion. |
| `durationComplexity` | Difficulty introduced by the shot's temporal span and sustained coherence. |

The validator computes an equally weighted `overallScore` as the rounded arithmetic mean of the eleven scores. It derives `overallBand` using fixed thresholds:

- `0..33`: `Low`
- `34..66`: `Medium`
- `67..100`: `High`

The canonical response also contains deterministic per-dimension `reasons` and optional submitted `evidence`:

```json
{
  "motionComplexity": 80,
  "cameraComplexity": 70,
  "faceImportance": 60,
  "handBodyInteractionComplexity": 40,
  "fineDetailImportance": 50,
  "environmentComplexity": 30,
  "vfxComplexity": 0,
  "continuitySensitivity": 90,
  "textSignageSensitivity": 0,
  "dialogueLipSyncDependency": 75,
  "durationComplexity": 40,
  "overallScore": 50,
  "overallBand": "Medium",
  "reasons": [
    {
      "dimension": "continuity_sensitivity",
      "score": 90,
      "band": "High",
      "reason": "continuity_sensitivity is high at 90/100.",
      "evidence": "The locked red notebook must remain visible."
    }
  ],
  "evidence": [
    {
      "dimension": "continuity_sensitivity",
      "reason": "Locked prop continuity is important.",
      "evidence": "The locked red notebook must remain visible."
    }
  ]
}
```

## Validation boundary

`MovieProductionComplexityValidator` is deterministic and provider-free:

- rejects missing dimensions rather than defaulting missing values to low complexity;
- rejects scores outside `0..100`;
- accepts only `Low`, `Medium`, and `High` bands;
- accepts only `Manual` and `DirectorProposal` sources;
- bounds evidence to 24 items, 500-character reasons, and 1,000-character evidence detail;
- rejects malformed JSON; and
- rejects contradictory profiles when an optional `declaredOverallBand` does not match the computed band.

Invalid profiles never create a database record. The saved record is a versioned, validated snapshot with a canonical JSON profile, aggregate score/band, source, actor, and timestamp.

## API and persistence

- `GET /api/movie-studio/shots/{shotId}/production-complexity` returns the latest validated assessment, or `204 No Content` when a permitted shot has no assessment yet.
- `PUT /api/movie-studio/shots/{shotId}/production-complexity` validates and appends a new assessment version. It requires the existing Movie edit permission and CSRF protection.
- Existing `MovieShotDto` responses expose the latest assessment as nullable `productionComplexity`.
- Director context snapshots expose the latest profile on each `DirectorShotContext`.
- Director proposals may carry an optional `productionComplexity` request. The same validator runs before proposal persistence; the canonical profile is included in the plan item and action payload. Proposal validation does not mutate the shot until a future approved workflow explicitly chooses to apply it.

Migration `AddMovieProductionComplexity` creates `MovieProductionComplexityAssessments` with a unique `(MovieShotId, Version)` index and a cascade relationship to `MovieShots`. It is additive and does not alter existing movie UX or provider tables.

## Explicit non-goals and limitations

- No Adaptive Resolution Director exists in this wave; this profile is its bounded input contract.
- No resolution, frame-rate, provider selection, vendor capability, or provider-cost logic is derived from these scores.
- The current aggregate is equally weighted. Future adaptive logic may choose a different policy without changing the stored per-dimension contract.
- Contradiction detection currently covers declared aggregate-band conflicts and schema/category conflicts. It does not infer domain contradictions from natural-language descriptions; evidence remains explainable metadata rather than an AI judgment.
- Assessment history is append-only through the API. There is no assessment approval/rejection workflow in this wave.
- Charging remains off and video providers remain off; saving a profile creates no Generation Job or Usage transaction.
