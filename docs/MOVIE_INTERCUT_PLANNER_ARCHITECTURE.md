# Movie Studio Wave 5 — Intercut Planner

## Scope

The intercut planner is a **review-only Director planning seam** for independently generated or independently prepared shots. It composes canonical shot IDs into a deterministic `MovieCanonicalTimelineContract`; it does not create media, copy assets, enqueue generation, write a timeline revision, or charge usage.

The input candidates are deliberately provider-neutral and contain only editorial identity and timing:

- canonical scene and shot IDs;
- scene/shot story-order coordinates;
- coverage kind: `primary`, `reaction`, or `insert`;
- optional anchor shot for reaction/insert coverage;
- bounded duration and an optional user-facing label.

## Ordering rules

1. Primary coverage is sorted by `(sceneSequence, shotSequence, clipId)` and is the authoritative story-order spine.
2. Reaction and insert coverage may be intercut around the spine. An anchor moves the coverage to the anchored shot's story position; otherwise its own scene/shot position is used.
3. Coverage candidates never silently reorder primary beats.
4. A requested order that changes primary beats is returned as a proposal with `requiresUserApproval = true`; the proposed timeline remains in canonical story order.
5. `MovieIntercutPlanner.ApplyUserApproval` is the explicit, pure approval boundary. It requires a non-empty reason and returns a new canonical timeline version. It does not persist or execute the timeline.

## Provenance and safety

Proposal and timeline identifiers are deterministic for the same project, base version, candidates, and requested order. A SHA-256 provenance hash is included for recovery and downstream hand-off. Normal-user DTOs contain no provider, model, prompt, credential, cost, or execution fields.

The endpoint is authenticated and uses the existing `director.proposal.create` authorization policy:

```text
POST /api/movie-director/projects/{movieProjectId}/intercut-proposals
```

The endpoint is CSRF-protected and returns a review proposal only. It does not add a database entity or migration. Applying a proposal to the persisted timeline remains an explicit future editorial operation over the existing revision/locking service.

## Test coverage

`MovieIntercutPlannerTests` covers deterministic composition, reaction/insert intercut ordering, story-order change review gating, explicit approval, invalid anchors, duplicate primary coverage, canonical transition construction, and normal-user DTO metadata cleanliness.
