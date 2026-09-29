# Movie Wave 2 — Cinematography Planning Intelligence

## Scope

Movie shots now have a deterministic, provider-neutral cinematography planning boundary. The planner turns bounded Movie context into a canonical `CinematographyShotPlan`; it does not produce a prompt, select a provider, call a provider, generate media, or queue a Generation Job.

The existing `CinematographyIntentSelection` and preset catalog remain backward-compatible Shot Designer inputs. The new plan is persisted in the existing `MovieShots.CinematographyJson` column, so no migration or provider activation is required.

## Canonical shot-plan contract

`CinematographyShotPlan` contains only closed structured values for:

- `shotSize`
- `framing`
- `cameraAngle`
- `cameraPosition`
- `cameraMovement`
- `compositionIntent`
- `lensLookIntent`
- `depth`
- `focusIntent`
- `lightingIntent`
- `subjectEmphasis`
- `visualTransitionIntent`

It also carries optional bounded `creativeNotes`, bounded structured `grounding` references, and the authoritative locked Guide revision number when one was used. The value sets live in `CinematographyPlanningValues`; arbitrary values such as provider controls, model names, or free-form prompt fragments are rejected by `CinematographyShotPlanValidator`.

A typed `cinematographyPlan` is exposed on `MovieShotDto` in addition to the existing raw JSON and legacy summary fields. Legacy clients can continue using `cinematography` intent/presets.

## Planning endpoint

```text
POST /api/movie-studio/shots/{shotId}/cinematography/plan
```

The request accepts only bounded optional creative context (`characterEmotionalPurpose`, `creativeNotes`) and optional structured overrides under `overrides`. It does not accept an unrestricted prompt. The server resolves the shot, scene, Movie Project, Guide, approved Story when available, selected character context, prior shot, and scoped continuity facts.

The response reports:

- the canonical structured plan;
- `guideGrounded` and `lockedGuideRevisionNumber`;
- `canonPreserved`;
- the structured field names whose locked values were applied;
- grounding references showing the Story, Scene, Shot, aspect ratio, Guide/Cinematography Bible, character/emotional purpose, previous shot, and continuity inputs used.

This endpoint only updates the shot plan JSON. It does not call `IMovieVideoProvider`, create a clip, create a Generation Job, charge a customer, or select a provider/model.

## Deterministic grounding and precedence

The planner uses bounded keyword-to-value rules as a transparent, repeatable baseline. It considers:

1. approved Story premise/logline/synopsis/treatment and the screenplay scene linked to the Movie scene;
2. the Movie scene title, summary, and continuity notes;
3. shot description, purpose, subjects, dialogue, and visual continuity notes;
4. selected character performance, emotional, and continuity notes;
5. the project aspect ratio;
6. the locked Guide’s camera language, lighting, and cinematography Bible;
7. project/scene/shot continuity facts and the previous shot’s plan.

The precedence rule is strict:

> Locked cinematography canon wins over requested overrides and planner inference. The planner may fill only what canon leaves unspecified; it never silently changes a locked value.

Legacy locked Guide intent/preset selections are translated to canonical plan values for this merge boundary. A locked canonical plan stored in the Guide revision is preferred when available. Unlocking or revising the Guide remains the only way to change its canon.

## Tests

Focused verification:

```text
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --filter 'FullyQualifiedName~MovieCinematographyTests|FullyQualifiedName~MovieCinematographyPlanningTests'
```

Coverage includes closed-value validation and JSON round-trip, rejection of non-structured/provider-like values, Story/Scene/Guide/continuity grounding, locked-canon precedence, API authorization, persistence, and typed Movie shot read-back.
