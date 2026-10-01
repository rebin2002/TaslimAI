# Movie Sound Effects and Ambience Tracks

Wave 4 adds provider-neutral sound-effect (`sfx`) and ambience (`ambience`) tracks for movie scenes and shots.

## Contract

- Tracks are scoped to a `MovieProject` and target exactly one scene or shot.
- Cue timing is expressed as integer milliseconds: `startMilliseconds < endMilliseconds`.
- Fade-in and fade-out durations must fit inside the cue. Scene/shot duration is enforced when known.
- Supported layers are `foreground`, `background`, `foley`, `room_tone`, and `environment`.
- Track lifecycle is `Draft -> Queued -> ReadyForReview -> Approved` (or `Rejected`/`Failed`).
- Approval is permission-gated and writes an immutable decision record to `MovieSoundApprovals`.
- Imported assets must be active audio assets in the movie workspace and are automatically reusable through a project sound-library reference.

## Endpoints

- `GET /api/movie-sound/projects/{movieProjectId}/library`
- `POST /api/movie-sound/projects/{movieProjectId}/library`
- `GET|POST /api/movie-sound/scenes/{sceneId}/tracks`
- `GET|POST /api/movie-sound/shots/{shotId}/tracks`
- `GET /api/movie-sound/tracks/{trackId}`
- `POST /api/movie-sound/tracks/{trackId}/review`

Mutating endpoints require the existing authenticated movie collaboration permissions and CSRF protection. Generation requests are rate-limited through the existing expensive-AI policy.

## Generation seam

Sound generation uses `IMovieSoundProvider` and the existing `GenerationJob` lifecycle. `FakeMovieSoundProvider` emits a deterministic silent WAV for execution tests and has zero cost; the default `MovieSoundGeneration` configuration is disabled and resolves to `UnavailableMovieSoundProvider`. No real sound provider or model is enabled by this change.

Worker output validation accepts only bounded `mp3`, `wav`, `ogg`, or `flac` payloads with a matching audio signature. The asset result is published through the existing private generated-asset pipeline, and provider/model identifiers remain server-side job metadata rather than normal-user track DTO fields.

## Provenance

Each track records a JSON provenance envelope containing its source kind (`generated`, `imported`, or `library`), source asset when applicable, generation job when applicable, creating user, and timestamp. The public DTO exposes source kind and references but not provider prompts or provider/model identifiers.
