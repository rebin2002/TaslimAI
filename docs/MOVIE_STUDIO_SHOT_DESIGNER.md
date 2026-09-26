# Movie Studio V2 — Shot Designer Foundation

## Product modes

- **Normal mode:** a movie brief chooses one of four intent presets: Intimate, Natural, Epic, or Dynamic.
- **Advanced mode:** a scene card's Shot Designer captures shot size, focal-length intent, lens intent/type, aperture/depth-of-field intent, camera angle, movement, frame-rate intent, lighting, palette/look, and composition notes.

These are **production-intent fields**. They do not claim that a future video adapter exposes physical camera controls.

## Contracts

- `GET /api/movie-studio/cinematography/presets` returns the server-owned, provider-neutral preset catalog.
- `POST /api/movie-studio/scenes/{sceneId}/shots` accepts the existing shot fields plus `cinematography`.
- `POST /api/movie-studio/projects` accepts the optional project-level `cinematography` selection.
- `PATCH /api/movie-studio/projects/{id}/guide` accepts the same selection for later Bible edits.
- Movie project responses expose `guide.cinematographyBible` and shot responses expose `cinematographyJson`.

A selection includes `intent`, `presetId`, optional notes, structured controls, and capability references. Preset capability references are intentionally classified as **Translated** today; the catalog does not invent provider-native support.

## Persistence and snapshots

- `MovieContinuityGuides.CinematographyIntent` stores the stable project intent.
- `MovieContinuityGuides.CinematographyBibleReferencesJson` stores the normalized Bible selection and capability references.
- `MovieShots.CinematographyJson` stores the normalized shot selection.
- Scene/shot generation inputs and clip continuity snapshots carry these values forward so an adapter can resolve capability classifications without rereading mutable UI state.

## Future adapter integration

At capability-resolution time, each field should be mapped to one of:

1. **Native** — the selected adapter explicitly supports the production intent.
2. **Translated** — the adapter receives a supported equivalent instruction.
3. **Simulated/Post** — the result can be approximated or handled after generation.
4. **Unsupported** — the request cannot be honored and should be surfaced for review.

The adapter layer should consume `MovieVideoGenerationRequest.ShotJson` and continuity snapshots, resolve classifications, and record the resolution. It must not infer support from a preset name or expose provider/model names in normal mode.
