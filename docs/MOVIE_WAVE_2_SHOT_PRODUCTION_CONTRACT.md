# TASLIM AI — Movie Wave 2 Shot Production Contract

## Scope

This contract extends the existing `MovieShot` planning record for the production pipeline:

> Story → Scene → Shot → Storyboard → Production Keyframe → Motion Preview → Production Render → Take/Version → Selected Take → Master

Wave 2 adds **planning data only**. It does not create generation jobs, select providers, expose model names, calculate vendor costs, or change the production workflow.

The canonical API shape is `MovieShotDto.productionContract` and `MovieShotProductionDto.productionContract`. Its `schemaVersion` is currently `1`.

## Reuse audit

| Requirement | Existing support | Wave 2 treatment |
| --- | --- | --- |
| Duration | `MovieShot.DurationSeconds` | Reused; surfaced in the contract |
| Cinematography intent | `MovieShot.CinematographyJson`, `CameraAndFraming`, `CameraMotion`, `CinematographyIntentSelection` | Reused; decoded as `cinematography` |
| Planning status | `MovieShot.Status`, `ProductionStage`, `MovieShotReadiness.PlanState` | Reused; surfaced as `status`, `planningStatus`, and `productionStage` |
| Storyboard/keyframe/motion/render stages | `MovieProductionVersion`, `MovieProductionStages`, `MovieProductionWorkflow` | Reused unchanged |
| Approval and provenance | `MovieProductionVersion.Status`, `ReviewedByUserId`, `ReviewedAt`, `StageProvenanceJson`, source/continuity references, and `MovieProductionStageTransition` | Reused; latest version is summarized under `approval` |
| Take/version and selected/master state | `MovieTake`, `MovieShot.SelectedTakeId`, `MovieShot.FinalTakeId`, take approvals | Reused unchanged; `FinalTakeId` remains the current master/final pointer |
| Narrative importance | None at shot level | Added as a bounded canonical choice |
| Production complexity profile | None at shot level | Added as provider-neutral structured JSON |
| Quality requirements | Project/take quality exists, but no shot-specific acceptance contract | Added as provider-neutral structured JSON |
| Continuity sensitivity | Continuity references, notes, locks, and snapshots exist, but no shot-level sensitivity | Added as a bounded canonical choice |
| Upscale suitability | None | Added as a bounded canonical choice |
| Target output requirements | Project-level aspect ratio exists, but no shot-level target contract | Added as provider-neutral structured JSON |

## Contract shape

```json
{
  "schemaVersion": 1,
  "durationSeconds": 8,
  "narrativeImportance": "primary",
  "cinematography": {
    "intent": "intimate",
    "presetId": "intimate-naturalism",
    "notes": "Protect eyeline and breathing room."
  },
  "productionComplexity": {
    "level": "high",
    "drivers": ["camera_motion", "continuity", "performance"],
    "notes": "Repeatable blocking and lighting are required."
  },
  "qualityRequirements": {
    "minimumLevel": "Cinematic",
    "acceptanceCriteria": [
      "Readable facial performance",
      "Stable subject identity"
    ],
    "notes": "Protect the approved composition."
  },
  "continuitySensitivity": "high",
  "upscaleSuitability": "conditional",
  "targetOutputRequirements": {
    "aspectRatio": "16:9",
    "resolutionIntent": "uhd",
    "frameRateIntent": "24 fps",
    "colorIntent": "cinematic-neutral",
    "audioIntent": "stereo",
    "formatIntent": "video",
    "notes": "Intent only; the eventual adapter resolves capability."
  },
  "status": "Approved",
  "planningStatus": "Production",
  "productionStage": "ApprovedStoryboard",
  "approval": {
    "status": "Approved",
    "productionVersionId": "…",
    "stage": "ApprovedStoryboard",
    "sourceVersionId": "…",
    "reviewedByUserId": "…",
    "reviewedAt": "…",
    "stageProvenanceJson": "{…}",
    "continuitySnapshotId": "…",
    "continuitySnapshotVersion": 3,
    "continuitySnapshotHash": "…",
    "cinematographyReferenceJson": "{…}"
  }
}
```

### Enumerations

The values are intentionally provider-neutral:

- `narrativeImportance`: `background`, `supporting`, `primary`, `critical`
- `productionComplexity.level`: `low`, `moderate`, `high`, `critical`
- `continuitySensitivity`: `low`, `moderate`, `high`, `locked`
- `upscaleSuitability`: `preferred`, `conditional`, `not_recommended`
- `qualityRequirements.minimumLevel`: existing `MovieQualityLevels` (`Fast`, `Standard`, `Cinematic`, `Studio`)

The profile and output objects are bounded and extensible. Their fields describe intent and acceptance criteria, not a provider's feature set. Unknown future fields should be introduced through a schema-versioned contract revision rather than vendor-specific properties.

## Persistence

The six new nullable `MovieShots` columns are:

- `NarrativeImportance` (`varchar(20)`)
- `ProductionComplexityJson` (`varchar(20000)`)
- `QualityRequirementsJson` (`varchar(20000)`)
- `ContinuitySensitivity` (`varchar(20)`)
- `UpscaleSuitability` (`varchar(20)`)
- `TargetOutputRequirementsJson` (`varchar(20000)`)

Nullable storage preserves all existing Wave 1 shots. No backfill invents a priority, complexity, quality bar, continuity lock, upscale decision, or output target.

## Lifecycle and downstream use

1. **Story/scene planning** may populate narrative importance, complexity, quality, continuity sensitivity, upscale suitability, and output intent.
2. **Storyboard** continues to use `MovieProductionVersion` at `StoryboardCandidate` / `ApprovedStoryboard`.
3. **Production keyframe** and later stages continue to use `MovieProductionVersion.SourceVersionId`, continuity references, and cinematography references.
4. **Approval/provenance** remains version-level and transition-level. The shot contract's `approval` member is a read model of the latest production version; it is not a second approval system.
5. **Take/version selection** continues to use `MovieTake`, `SelectedTakeId`, and `FinalTakeId`. `FinalTakeId` is the current master/final pointer; Wave 2 does not add another master entity.
6. The existing future-facing `ShotJson` snapshot now carries the complete `productionContract`, so later adapters can consume one stable request-level shape without rereading mutable UI state.

For tasks 4–6 and the future Adaptive Resolution Director, consume the contract and resolve capabilities in AI Core/provider routing. The resolver may classify each intent as native, translated, simulated/post, or unsupported, but that classification does not belong in core Movie persistence.

## Compatibility notes

- Existing request payloads remain valid; all new request members are optional.
- Existing response members remain in place; `productionContract` is an additive response member.
- Existing `MovieProductionVersion`, `MovieTake`, selected/final take, approval, and transition semantics are unchanged.
- Existing nullable values remain nullable. Missing Wave 2 planning data means **unspecified**, not a default production decision.
- No provider, model, vendor capability, or premature cost field is present in `MovieShot` or the stable contract.
- Charging and provider readiness are unaffected. This change does not queue generation.
