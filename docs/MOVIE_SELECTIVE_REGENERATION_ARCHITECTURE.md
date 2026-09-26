# Movie Studio V2 Selective Regeneration

Selective regeneration revises one **shot-level production element** without deleting or overwriting prior production records. It is intentionally not pixel-level or object-level editing: the provider boundary still receives a complete shot-clip request, and the result is stored as a new candidate version/take.

## Lifecycle

1. `POST /api/movie-studio/shots/{shotId}/regeneration-requests` validates the target shot, requested production stage, source version, reason, changed-input manifest, and composition. It persists a `MovieRegenerationRequest` with a cost preview and creates no Generation Job.
2. `POST /api/movie-studio/regeneration-requests/{requestId}/confirm` requires `{ "confirm": true }`. Only this explicit confirmation queues the existing `movie.clip.generate` Generation Job. The request records the job, candidate `MovieProductionVersion`, and resulting `MovieTake`.
3. `GET /api/movie-studio/regeneration-requests/{requestId}` and shot production history expose the immutable request audit trail and safe job/version/take identifiers.

Supported action types are storyboard candidate, production keyframe candidate, motion preview, production render, character continuity, world continuity, and cinematography. Each action remains scoped to one shot and carries `target`, `reason`, `sourceVersionId`, and `changedInputsJson` in the request and job input.

## History and invalidation

All candidates, takes, Generation Jobs, and regeneration requests are append-only. No prior artifact is deleted or overwritten. When an upstream stage is regenerated, approved/selected downstream production versions are marked `ReviewRequired`; their linked takes are also marked `ReviewRequired` and deselected without deleting them. An `invalidated` production transition records the reason, source version, changed inputs, actor, and request lineage.

## Cost and provider safety

The request uses the existing Wave 5 `IGenerationCostEstimator` and stores the estimate snapshot for preview. The existing worker invokes `IGenerationBudgetService` before provider execution, so configured ceilings and unknown-estimate policy remain authoritative. Customer charging remains disabled. The selective flow never calls a provider directly; it queues the existing movie job path. With the default unconfigured movie provider, the job fails safely without a paid provider call.
