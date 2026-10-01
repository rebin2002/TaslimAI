# Movie Dialogue & Voice Production Foundation

Wave 4 adds a provider-neutral dialogue layer for movie clips. It is intentionally separate from the existing visual `MovieTake` model: visual take lifecycle and audio dialogue take lifecycle can evolve independently while sharing the same durable Generation Job, Asset, storage, authorization, and usage foundations.

## Canonical domain

- `MovieDialogueLine` belongs to one `MovieClip` and stores sequence, speaker label, optional `MovieCharacter` linkage, language, source text, and millisecond timing.
- `MovieDialogueTake` belongs to one line and clip. It records version, lifecycle state, Generation Job, Asset, StoredFile, safe output metadata, and usage metadata.
- `MovieDialogueTakeApproval` is append-only review history. A take must be completed and approved before selection.
- A line has at most one selected take reference. Selecting a new approved take supersedes the previous selected take; no audio asset is deleted.

The DTOs expose only user-safe production state. They do not expose provider keys, model keys, prompts, credentials, or upstream response fields.

## Lifecycle

1. Add a draft dialogue line to a clip after movie edit permission is checked.
2. Queue a versioned voice take after movie generate permission is checked. The request is persisted as `movie.dialogue.voice.generate` and passes the shared cost guardrail and usage ledger.
3. The worker marks the take running, invokes the configured provider-neutral adapter, publishes an audio Asset through the shared publisher, and marks the take succeeded with Asset/StoredFile provenance.
4. The owner or an authorized reviewer records an approval or rejection. Only an approved take can be selected.
5. Selecting a take marks the old selected take superseded and updates the line reference.

`MovieDialogueVoiceOptions.Enabled` defaults to `false`. The default adapter is disabled and performs no external call. A deterministic in-process adapter exists only for tests and local lifecycle validation; no real voice adapter is registered by this change.

## Contracts and seams

`IMovieDialogueVoiceProvider` accepts a structured request containing clip/line/character linkage, speaker, language, text, delivery notes, and timing. It returns audio bytes plus normalized duration and usage metadata. The job handler validates both input and output before the shared Asset publisher stores the artifact. The provider/model fields remain internal to the Generation Job and ledger boundary.

Actual or estimated cost is carried through `AiUsageMetadata` and the existing `UsageLedgerService`; the dialogue tables retain only safe usage metadata for audit/debugging. This keeps future provider adapters replaceable without changing movie APIs or normal-user UI contracts.

## HTTP surface

- `GET /api/movie-studio/clips/{clipId}/dialogue`
- `POST /api/movie-studio/clips/{clipId}/dialogue`
- `POST /api/movie-studio/dialogue/{lineId}/takes`
- `POST /api/movie-studio/dialogue/takes/{takeId}/approval`
- `POST /api/movie-studio/dialogue/takes/{takeId}/select`

All mutations require the existing anti-forgery boundary and server-side movie collaboration permissions. Queueing accepts the existing `Idempotency-Key` header.

## Migration and integration notes

`AddMovieDialogueVoiceFoundation` adds three tables and foreign keys only. It does not alter existing visual movie take tables or activate any external provider. Integrators should avoid conflating `MovieDialogueTake` with `MovieTake`; an assembly pipeline should consume the selected audio Asset through its stable Asset/StoredFile reference.
