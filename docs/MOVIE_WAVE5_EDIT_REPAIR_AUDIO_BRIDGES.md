# Movie Wave 5: Director edit-repair audio bridges

## Purpose

The Director can now produce a bounded **audio edit-repair plan** for an existing canonical timeline. The plan is intended to salvage an edit with small sound decisions rather than regenerate an entire visual scene:

- fill uncovered picture gaps with existing approved ambience or music;
- carry approved room tone or music across hard visual cuts;
- use an existing approved transition SFX as a short smoothing insert;
- support an existing dissolve or fade without changing the visual transition.

The planner uses only provider-neutral snapshots of existing SFX, ambience, music, library, and soundtrack-cue capabilities. It does not infer provider/model/prompt information and does not create new media.

## Director contract

`DirectorProposalRequest` accepts the additive fields:

- `planEditRepairAudio` or `editRepairAudioAction: "plan_audio_bridges"`;
- `canonicalTimeline`, validated by the existing `MovieTimelineValidator`;
- `audioCapabilities`, containing only already available, approved capability snapshots.

The Director proposal carries an `editRepairAudio` review object. Each recommendation is tied to the canonical timeline id/version and includes a bounded range, source kind/id when available, rationale, and `requiresUserOverride: true`.

## Authority and safety

The audio plan is advisory. Approval moves the normal Director proposal/action state forward, but execution only records the validated plan result:

- `providerCalled: false`;
- `canonicalTimelineMutated: false`;
- `userOverrideRequired: true`.

The executor does not call SFX, ambience, music, soundtrack, generation-job, assembly, or billing services. The canonical timeline and its existing user-override workflow remain authoritative. A future UI or persistence adapter must create an explicit user-owned audio/timeline edit before any recommendation becomes assembly input.

## Compatibility and migration

This change is additive and stores the bounded plan in the existing `DirectorAction.PayloadJson` field. No database migration is required. Existing Director actions, canonical timeline revisions, soundtrack cues, sound tracks, provenance, accounting, and selected-take-only mastering remain unchanged.

## Deterministic tests

`MovieDirectorEditRepairAudioTests` covers gap filling, hard-cut smoothing, approved music reuse, provider-disabled execution, user-override requirements, and provider-neutral soundtrack capability mapping. No external media provider is used.
