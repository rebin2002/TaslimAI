# Movie Production Continuity Review

Movie production continuity review extends the existing typed Story consistency finding contract to planned scenes and shots. It is a **read-only diagnostic**: it never edits Story, Guide, Scene, Shot, Cast, World, or production-version records.

## Review surface

The API exposes:

| Method | Route | Scope |
| --- | --- | --- |
| `GET` | `/api/movie-studio/projects/{movieProjectId}/continuity-review` | Project plan, optionally narrowed with `sceneId` and/or `shotId` |
| `GET` | `/api/movie-studio/scenes/{sceneId}/continuity-review` | One scene and its planned shots |
| `GET` | `/api/movie-studio/shots/{shotId}/continuity-review` | One shot, with its containing scene's shot order for chronology |

The response is `MovieProductionContinuityReviewDto` and always carries `reviewOnly: true` plus a bounded list of `DirectorStoryFindingDto` findings.

## Canon inputs

The bounded context reads only persisted project records:

- planned scene and shot text, including subjects, explicit subject character IDs, location/set, production requirements, continuity references, and visual continuity notes;
- Character cards, states, wardrobe, injuries/conditions, locations/story states, and character continuity locks;
- World usages plus bounded Location, Set, Set Variation, Prop, Continuity Fact, and active Continuity Lock projections;
- approved Story-to-production scene links for chronology comparison;
- the locked Guide revision's continuity/story/world sections and continuity rules.

Missing canon is not inferred. Text checks report only explicit opposing terms or explicit conflicts between structured planning fields and persisted usage/lock records. The context is bounded by scene, character, usage, story-link, Guide-section, and finding limits.

## Finding taxonomy

Findings reuse `DirectorStoryFindingDto` so the UI receives the same evidence/target/explanation/correction/confidence/uncertainty contract as Story consistency review.

- `hard_continuity_conflict`: active hard locks, locked Guide rules, ambiguous sequence identity, or explicit structured plan contradictions that cannot be safely ignored;
- `possible_inconsistency`: an explicit mismatch against an unlocked or potentially stale canon record, presence-list mismatch, or Story/production sequence discrepancy;
- `creative_suggestion`: a non-blocking handoff suggestion, such as adding a continuity reference when a shot names continuity-sensitive canon without an explicit reference.

Categories cover character presence, wardrobe, injury/state, environment/location, time of day, weather, object state, object position, chronology, World continuity, and locked Guide facts. Each finding includes source evidence from the plan and canon, an affected scene/shot/project target, a possible correction, a bounded confidence score, and uncertainty where the canon is not authoritative.

## Safety boundaries

The review service has no write methods, does not create snapshots or production versions, does not enqueue Generation Jobs, does not call a media provider, and does not touch charging or usage accounting. Existing Character and World projectors remain the source of bounded continuity records; the new analyzer only converts those records and plan text into review findings.
