# Movie Story-to-Scene Grounding Context

## Scope

`MovieStoryScenePlanningContextAssembler` is a read-only bounded projection for future scene planning. It does **not** generate scenes, create production `MovieScene` rows, mutate Story revisions, call providers, or add an approval workflow.

The existing `MovieDirectorContextAssembler` exposes the same service through:

- `AssembleStoryScenePlanningAsync`
- `AssembleScenePlanningAsync` (alias)

## Context contract

`MovieStoryScenePlanningContextDto` contains only:

- `movieProjectId` and `workspaceId`;
- the bounded Movie brief;
- target runtime seconds, language, and aspect ratio;
- the locked Guide revision and scene-planning Guide facts;
- bounded current Story revision material when present;
- bounded approved Story revision material when present;
- the target screenplay scene identity;
- only the target scene and its bounded surrounding screenplay scenes;
- explicit `missingSections` diagnostics.

A Story revision projection includes premise, logline, synopsis, treatment, and at most five ordered screenplay scenes. Each scene includes at most six ordered screenplay elements. No revision history, production hierarchy, Cast graph, World graph, comments, reviews, or provider metadata is included.

The request can select:

| Field | Behavior |
| --- | --- |
| `storyRevisionId` | Must be the current or approved revision; an older revision is rejected as stale. |
| `targetSceneId` | Selects a persisted screenplay scene from the current or approved revision. |
| `targetSection` | Selects by exact scene identifier or slugline. |
| `surroundingSceneRadius` | Clamped to 0–3; the serialized projection remains capped at five scenes per revision. |
| `targetRuntimeSeconds` | Optional planning runtime, clamped to 1–86,400 seconds; defaults to the Movie Project runtime. |
| `language` / `aspectRatio` | Optional explicit planning values; defaults to the Movie Project values and is bounded before serialization. |

If Story or screenplay material is absent, assembly succeeds with null revision/target projections and `missingSections` entries rather than inventing content. A missing explicit target is rejected.

## Guide relevance

The locked Guide revision is required. The five project-level scene-planning fields are bounded directly. Locked Guide sections are filtered before serialization:

- Visual, cinematography, audio, and continuity sections are always scene-planning relevant;
- Story, character-reference, and world-reference sections are included only when they contain a target anchor (or when no target exists).

This keeps irrelevant Guide material out of the planning snapshot while retaining locked creative constraints.

## Determinism and diagnostics

The context omits wall-clock assembly time. Collections are ordered by Story revision pointer, screenplay ordinal, element ordinal, and fixed Guide section order. The UTF-8 serialized snapshot is hashed with lowercase SHA-256. Identical source state and request therefore produce identical `snapshotJson` and `snapshotHash`.

The result also returns `MovieStoryScenePlanningContextDiagnostics` with:

- max, used, critical, and optional byte counts;
- critical-facts completeness;
- target resolution;
- revision, scene, element, and Guide-section counts;
- missing sections and included source kinds;
- assembly duration outside the hashed snapshot.

The hard snapshot maximum is **100,000 UTF-8 bytes**. The assembler fails with `MovieStoryScenePlanningContextBudgetException` instead of emitting an over-budget context.

## Stale-target protection

A target scene from a superseded Story revision is rejected with `MOVIE_SCENE_PLANNING_STALE_TARGET`. Scene planning may only be grounded in the current or approved Story pointer. A target from another Story/project is rejected with `MOVIE_SCENE_PLANNING_TARGET_NOT_FOUND`.

## Validation

`MovieStoryScenePlanningContextTests` covers:

- Last Seed brief, runtime, language, aspect ratio, target, surrounding screenplay, and deterministic hash;
- screenplay relevance and history exclusion;
- 100,000-byte bound and per-scene element bound;
- superseded target rejection;
- missing Story/screenplay diagnostics without fake scene material;
- explicit runtime, language, and aspect-ratio fields.
