# Movie Shot Quality Requirements Profile

## Scope

Each `MovieShot` now carries a provider-neutral, deterministic quality profile. The profile describes what must be preserved in a shot; it does not select an output resolution, provider, model, credits, or a generation path.

The profile is grounded in the shot plan plus bounded project/scene context:

- description, purpose, subjects, location/set, production requirements;
- continuity references and visual continuity notes;
- camera/framing, camera motion, cinematography, dialogue, narration, and duration;
- project title, description, style, and available visual/camera/color/continuity guide text;
- bounded character and world continuity anchors.

## Contracts

`MovieShotQualityRequirementsDto` contains exactly twelve requirements. Each requirement has:

- `key`: stable snake-case dimension key;
- `score`: integer from `0` through `4`;
- `level`: deterministic `none`, `low`, `moderate`, `high`, or `critical` mapping;
- `required`: deterministic (`score >= 2`);
- `rationale`: bounded explanation grounded in the shot content.

Supported keys:

- `facial_fidelity`
- `hand_body_fidelity`
- `temporal_consistency`
- `character_consistency`
- `environment_consistency`
- `fine_detail_preservation`
- `motion_fidelity`
- `text_signage_fidelity`
- `lip_sync_requirement`
- `vfx_fidelity`
- `upscale_suitability`
- `continuity_fidelity`

`MovieAdaptiveResolutionDirectorInputDto` is the clean downstream input. It contains the project/scene/shot identity, shot description, project style, quality profile, preservation priorities, and continuity anchors. It contains no provider, model, resolution, credit, charging, or generation decision.

The existing `MovieShotDto` exposes both `qualityRequirements` and `adaptiveResolutionDirectorInput`. `MovieGenerationInput.ShotJson` carries the same provider-neutral values for future downstream consumption.

## Deterministic behavior

The planner uses bounded keyword and structured-field signals; it does not call an AI provider. Examples:

- explicit close-up/portrait/face content produces a critical facial-fidelity score;
- a background landscape/establishing shot does not receive a maximum facial-fidelity score;
- dialogue/speaking content activates lip-sync requirements;
- explicit signs, labels, logos, or readable writing activates text/signage fidelity;
- explicit VFX terms activate VFX fidelity;
- camera/subject motion and longer planned duration raise temporal and motion requirements;
- continuity references, guide continuity, and character/world anchors raise continuity requirements.

Validation rejects unsupported keys, duplicates, missing dimensions, out-of-range scores, mismatched levels, mismatched required flags, oversized grounding, and an adaptive input that is not aligned with the profile.

## Persistence and migration

`MovieShots.QualityRequirementsJson` stores the validated profile. The additive migration is:

- `20260929170000_AddMovieShotQualityRequirements`

New and updated shots are planned before persistence. Legacy shots without a stored profile are deterministically projected at read/snapshot time using the available shot/project context; they are not silently assigned a provider or resolution.

## Safety boundary

Charging remains disabled. Movie providers remain disabled by default. This change creates no generation job, provider execution, cost estimate, credit calculation, or external call.

## Limitations

- Content grounding is deterministic and text/field based; it is not visual analysis.
- The profile is a preservation requirement contract, not a promise that a future provider can satisfy every requirement.
- Existing legacy shots are projected lazily rather than backfilled in the migration; a future maintenance operation may materialize them if desired.
- Adaptive Resolution Director implementation and actual output-resolution selection remain out of scope.
