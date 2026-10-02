# Movie Wave 5 — Structured Camera Profile

## Scope

Movie cinematography now has an additive, provider-neutral **Camera Profile** read model for each shot. It makes the production intent explicit without selecting a provider, model, prompt, generation path, resolution, cost, or customer charge.

The profile covers:

- shot size;
- camera angle and movement;
- focal-length intent and lens intent;
- depth-of-field intent;
- lighting intent;
- exposure/look;
- bounded continuity constraints.

The existing `CinematographyShotPlan`, legacy cinematography selection, `CinematographyJson` column, preset routes, and shot summary fields remain supported.

## Precedence and audit

Planning is deterministic and server-grounded:

1. locked Movie Guide camera canon;
2. explicit per-shot Camera Profile override;
3. existing structured shot override;
4. planner inference from Story, Scene, Shot, aspect ratio, previous shot, character context, and continuity facts.

> A locked Movie Guide value always wins. A user override is never silently treated as authoritative when it conflicts with locked canon.

Planning responses include `overrideAudit` with requested fields, applied fields, fields blocked by locked canon, and the locked Guide revision. Persisted plans retain `profileSource`, `userOverrideFields`, and `lockedCanonFields` so downstream production stages can understand the hand-off without rereading mutable UI state.

## API

The existing route remains compatible:

```text
POST /api/movie-studio/shots/{shotId}/cinematography/plan
```

A semantic alias is also available:

```text
POST /api/movie-studio/shots/{shotId}/camera-profile
```

Both routes require the existing authenticated Movie permission and CSRF boundary. The request accepts bounded `cameraProfile` fields under the existing planning request. The response includes the canonical plan, typed `cameraProfile`, and audit data.

Shot DTOs expose `cameraProfile` in addition to legacy `cinematographyJson` and `cinematographySummary`. Storyboard summaries expose the same profile when available.

## Persistence and compatibility

No migration is required. The canonical structured plan is stored in the existing bounded `MovieShots.CinematographyJson` column. New fields are optional in the serialized plan so existing structured plans continue to deserialize. Legacy preset selections are projected into a safe Camera Profile read model until a shot is explicitly planned through the structured planner.

No generation job, provider call, external media call, accounting movement, upscaling request, or selected-take/mastering behavior is changed by this task. Customer charging and real movie/video/image/voice/music/SFX providers remain disabled.

## Tests

Focused backend coverage is in `MovieCinematographyPlanningTests` for:

- closed-value validation of exposure/look;
- focal-length and continuity profile round-trip behavior;
- locked Guide precedence over focal, exposure, and movement overrides;
- explicit override audit fields and profile source.

The existing `MovieCinematographyTests` continue to cover route authorization, persistence, legacy preset compatibility, and typed shot read-back.
