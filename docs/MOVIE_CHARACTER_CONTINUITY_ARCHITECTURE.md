# Movie Character Continuity Engine

## Scope

The Character Continuity Engine is the single server-side projection boundary for character cards, character states, character continuity locks, screenplay/scene/shot text, and production artifacts. It does not create another Director, Generation Job, provider, or visual-QC system.

## Target-scoped projection

`MovieCharacterContinuityService` resolves a target as one of:

- project;
- scene in that project; or
- shot in that project, optionally paired with its parent scene.

A shot/scene from another Movie Project is rejected before any character query. Candidate characters are limited to the target’s persisted approved screenplay character names and target scene/shot text. The projection then loads at most 32 candidate cards, at most 8 reference assets per character, and at most 12 applicable facts per character. It never copies the whole project cast into a generation payload.

The projection includes only bounded card fields, the relevant/current state, scoped facts, reference asset IDs, and authoritative character locks. It returns explainable warnings when deterministic persisted data conflicts.

## Deterministic warnings

Warnings include a stable code, severity, character ID/name, target scene/shot IDs, field, expected and actual values, source type/ID, and a human-readable message. Current checks cover:

- card-scoped and state-scoped lock/value conflicts;
- multiple persisted states matching the same target;
- scene/shot state mismatch when both are deterministically present in persisted target text;
- conflicting scoped fact values for the same character key;
- a primary reference asset not present in the persisted reference set.

No warning claims visual inspection. Generated imagery is not inspected unless real QC data is persisted by the existing QC system.

## Immutable snapshots

`MovieCharacterContinuitySnapshot` stores the bounded JSON, SHA-256 hash, target IDs, and monotonically increasing target-local version. Snapshot creation is exposed through:

- `GET /api/movie-studio/projects/{id}/continuity/characters?sceneId=&shotId=`
- `POST /api/movie-studio/projects/{id}/continuity/snapshots`
- `GET /api/movie-studio/continuity/snapshots/{snapshotId}`

Cross-workspace reads remain workspace-authorized, and cross-project target references are rejected.

Movie clips and production versions carry the immutable snapshot ID/version/hash alongside the existing compatibility JSON. Storyboard, keyframe, motion-preview, and render candidates therefore reference the exact continuity context used at creation without duplicating Generation Job records.

## Director contract

The existing `DirectorContextDto` now has an optional `Continuity` member containing the selected target’s snapshot hash, target IDs, bounded character projection, and warnings. Director proposal creation resolves continuity for its selected shot. The Director remains the same proposal/approval/action surface and continues to execute through the existing Movie Studio Generation Job path.

## Lock authority

Existing character card/state lock mutation checks remain authoritative. Continuity snapshots surface lock conflicts with their lock source; they do not silently rewrite or override locked facts. Project and world locks remain owned by their existing Movie World services.
