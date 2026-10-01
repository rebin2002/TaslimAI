# Movie Wave 5: Missing Insert and Coverage Planner

The Missing Insert Planner is a deterministic, provider-neutral read model for finding uncovered ranges in the canonical Movie timeline and proposing the smallest safe insert coverage.

## Endpoint

`GET /api/movie-studio/projects/{projectId}/insert-planner`

Optional query parameter: `revisionId` selects a specific timeline revision; otherwise the canonical current revision is inspected.

The endpoint is authenticated and Movie-project authorized. It does not create a shot, modify a timeline, create a Generation Job, invoke a provider, charge a customer, or request upscaling.

## Grounding and safety

A proposal is emitted only when all of the following are present:

- an approved Story revision links the neighboring production scene to a screenplay scene;
- a locked Movie Guide revision exists; and
- an existing Production Kit can be read for a neighboring shot.

Proposals copy only bounded, existing shot-plan fields such as subjects, location/set, camera language, production requirements, and continuity references. Their description explicitly identifies the existing neighboring shot and edit gap. The planner never invents a character, location, prop, event, dialogue, or story beat.

The response marks every proposal `PendingApproval`, sets `RequiresApproval` to `true`, and sets `ChangesStoryCanon` to `false`. Approval/application is outside this read-only planner boundary and must use the existing Movie shot, review, selection, and timeline workflows.

## Gap and continuity behavior

- Explicit `Gap` items and uncovered ranges between items on the active video track are returned as `TimelineGap` records.
- Muted and non-video tracks do not define picture coverage.
- Very short ranges are reported but not proposed.
- Same-scene adjacent shots with different location/set fields produce a conservative continuity review finding; the planner does not infer that the difference is an error or change the Story.
- If grounding is incomplete, gaps and safe warnings remain visible but proposals are withheld.

No migration is required. The planner composes existing Movie Story, Guide, Production Kit, shot, take, and canonical timeline records.
