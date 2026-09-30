# Movie Wave 4 — Caption and Subtitle Tracks

## Scope

This additive feature provides project-scoped caption/subtitle tracks for the canonical Movie V2 hierarchy. It stores millisecond time ranges as the timeline authority and exposes stable timecode strings at the API and interchange boundaries.

Each `MovieCaptionTrack` carries:

- `Language` as a normalized BCP-47 tag and explicit `IsRtl` metadata;
- `TrackType` (`Subtitle` or `Caption`), status, default-track intent, source format, and source filename;
- an optional link to a canonical `MovieAssembly`.

Each `MovieCaptionCue` carries:

- start/end milliseconds with strict positive duration and non-overlap validation per track;
- text and optional speaker name/`MovieCharacter` linkage;
- optional canonical `MovieScene`, `MovieShot`, and `MovieTake` links for timeline provenance.

## Import/export seam

`IMovieCaptionFormatAdapter` is the provider-neutral interchange seam. The production registration contains only local deterministic adapters:

- `SrtMovieCaptionFormatAdapter` for SubRip (`.srt`);
- `WebVttMovieCaptionFormatAdapter` for WebVTT (`.vtt`), including optional voice tags.

The service accepts adapters through dependency injection, so tests can use an internal fake adapter without contacting a translation, media, or generation service. No translation is performed; imported text is persisted exactly after format parsing and bounded validation.

## API surface

All routes are authenticated and use the established Movie collaboration permissions (`View` for reads/exports and `Edit` for mutations/imports):

- `GET /api/movie-studio/projects/{movieProjectId}/caption-tracks`
- `GET /api/movie-studio/projects/{movieProjectId}/caption-timeline`
- `POST /api/movie-studio/projects/{movieProjectId}/caption-tracks`
- `GET /api/movie-studio/caption-tracks/{trackId}`
- `POST /api/movie-studio/caption-tracks/{trackId}/cues`
- `PATCH /api/movie-studio/caption-cues/{cueId}`
- `DELETE /api/movie-studio/caption-cues/{cueId}`
- `POST /api/movie-studio/projects/{movieProjectId}/caption-tracks/import`
- `GET /api/movie-studio/caption-tracks/{trackId}/export?format=srt|vtt`

The timeline read model flattens cues across tracks in chronological order while retaining track language, RTL direction, track type, and canonical cue links. It does not create a second scene/shot hierarchy.

## Safety and integration notes

- The migration is additive: `MovieCaptionTracks` and `MovieCaptionCues` are new tables with scoped indexes and nullable provenance foreign keys.
- Existing Wave 1–3 movie project, scene, shot, take, assembly, character, collaboration, and authorization contracts remain authoritative.
- Billing, customer charging, external translation, external media, and movie generation adapters are untouched and remain disabled by committed defaults.
- Caption serialization is deterministic and provider-free. Import/export does not enqueue a `GenerationJob` or call an external service.
- Future final assembly can consume the optional `MovieAssemblyId` track link without changing cue identity or timecode storage.
