# Movie Director Scene Planning

Scene planning is an additive action in the existing Movie Director proposal architecture. It does not create a second Director or provider system.

## Flow

1. `POST /api/movie-director/projects/{movieProjectId}/proposals` with `scenePlanAction: "plan_scenes"` (or `planScenes: true`) assembles the locked Guide plus the current/approved Story and screenplay, target duration, aspect ratio, language, Cast, World, and continuity facts.
2. The existing AI Core boundary returns strict structured scene-plan JSON. No deterministic creative fallback is used when creative AI is unavailable or malformed.
3. The response is persisted as a `PendingApproval` Director proposal with a `scene_planning` action. `scenePlan` is review-only data in the proposal DTO.
4. Approval changes only the existing proposal/action statuses. It does not create or update production scenes.
5. The existing explicit `POST /api/movie-director/actions/{actionId}/execute` boundary reassembles and hash-checks the context, revalidates runtime/grounding, then creates or updates the existing `MovieScene` entities.

## Scene-plan shape

Each scene carries title, narrative purpose, story beat, location/environment, time of day, participating characters, emotional objective, estimated duration, transition relationship, continuity requirements, production intent, and optional screenplay/production scene references. The action payload also retains language, aspect ratio, selected quality tier, Story base revision, and bounded context hash.

## Deterministic validation

- target duration must match the Movie Project duration;
- total scene duration must be at least 80% and no more than the target duration;
- a 30-second project is limited to six scenes;
- scene fields, character references, source IDs, placeholder markers, language, Story/Guide/World/continuity anchors, and the existing 20,000-character Director action payload budget are checked;
- stale Story, Guide, Cast, World, or continuity context fails at apply and creates no production scenes.

## Persistence and providers

No migration is required. The review plan uses the existing bounded `DirectorAction.PayloadJson` and the applied output uses the existing `MovieScene` entity. Scene planning uses the existing `IChatCompletionService`/AI Core boundary only. Customer charging remains disabled and the Movie Video provider remains disabled by default.
