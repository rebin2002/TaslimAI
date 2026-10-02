# Movie Production Kit Core

Wave 5 adds a project-level **Production Kit** as the explicit hand-off between the approved Movie Guide and expensive production. It is provider-neutral and does not queue generation, select a model, expose prompts, change usage accounting, or enable charging.

## Authority and shape

`MovieContinuityGuide` remains the canonical source for story, visual, cinematography, audio, and continuity decisions. A Production Kit never copies those sections. Each append-only `MovieProductionKitRevision` stores the source `MovieGuideRevisionId`/revision number and SHA-256 source hash, then adds typed pointers to existing project/workspace records:

- cast and character states;
- locations, sets, props, and world references;
- continuity snapshots;
- approved production keyframes;
- existing workspace/project Assets.

Each pointer carries bounded provenance JSON, source revision when applicable, a stable source hash, required/optional intent, and a sort order. Provider/model/prompt/credential fields are not part of the normal-user contract.

## Lifecycle

A revision moves explicitly through `Draft → Review → Approved → Locked`. Revisions are append-only; a new draft can be created before the kit is locked. Review requires Movie production edit permission. Approval and locking require the established Movie production-review permission. Unlocking is an explicit recovery operation that returns the locked revision to `Approved` without deleting history.

Approval and locking require the readiness aggregate to pass. Readiness requires the referenced Movie Guide revision to be the currently locked Guide revision, every required pointer to be resolved, and all pointer records to belong to the same Movie project/workspace. Readiness is returned even for drafts so the UI can explain what is missing.

## API

All routes are authenticated and project-authorized; state changes require the existing CSRF header:

- `GET /api/movie-studio/projects/{projectId}/production-kit`
- `POST /api/movie-studio/projects/{projectId}/production-kit/revisions`
- `POST /api/movie-studio/projects/{projectId}/production-kit/review`
- `POST /api/movie-studio/projects/{projectId}/production-kit/approve`
- `POST /api/movie-studio/projects/{projectId}/production-kit/lock`
- `POST /api/movie-studio/projects/{projectId}/production-kit/unlock`

The lifecycle commands accept an optional `revisionNumber`; only the current revision can transition. Cross-workspace/project sources return a safe validation error and never disclose source records.

## Migration and compatibility

`AddMovieProductionKit` is additive: existing Movie Guide, Cast, World, shot-production, selected-take-only mastering, timeline, provenance, recovery, and accounting tables remain authoritative. The migration creates one kit per Movie project, immutable revisions, and typed reference rows with restrictive user FKs and cascade only within the kit aggregate.
