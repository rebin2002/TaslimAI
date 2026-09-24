# Music Studio Architecture

Music Studio is a provider-independent foundation for creating, reviewing, and privately reusing generated music at `/create/music`. It adds the `music.generate` job type and the `music` Asset type path on top of the existing Taslim asynchronous generation, Asset Library, project, private storage, authentication, authorization, and zero-customer-charge accounting foundations. The workflow is complete even when no music provider is configured: the request is validated, queued, tracked, and then ends in a safe provider-unavailable state without exposing provider, model, storage, or cost internals to the user. The existing job and Asset boundaries are reused rather than duplicated.[1] [2]

## User workflow

An authenticated user provides a music description, purpose or use case, genre, mood, duration, instrumental or vocal preference, language, an optional project, and optional additional instructions. The browser sends only this bounded, user-facing request to `POST /api/music-generation/jobs`. The API returns an accepted Generation Job snapshot without provider or model fields. The browser polls the existing job snapshot endpoint until the job reaches a terminal state.

A successful provider execution returns a private music file. The worker stores that file through the configured local or S3-compatible private storage adapter, creates one `GenerationJobOutput`, and publishes one `music` Asset in the selected workspace and optional project. The completed result contains only safe playback metadata such as the Asset ID, title, format, duration, vocal preference, and language. The browser uses the authenticated Asset download endpoint for both inline playback and download. Asset authorization remains server-side.

A missing or disabled provider does not create a simulated track. The job fails asynchronously with `MUSIC_PROVIDER_UNAVAILABLE`, and the user sees only a localized message explaining that Music Studio is temporarily unavailable. Failed and cancelled jobs publish no Asset.

## Server boundaries

The backend is organized into five small boundaries.

| Boundary | Responsibility |
| --- | --- |
| `MusicGenerationRequestValidator` | Enforces bounded descriptions, purpose, title, instructions, supported genres, moods, durations, vocal preferences, and languages before queueing and again before execution. |
| `IMusicGenerationProvider` | Defines the provider adapter contract. Providers receive normalized user intent and return validated audio bytes plus internal usage metadata. The registered Mubert adapter is disabled until server-only credentials are configured. |
| `MusicGenerationJobHandler` | Deserializes and validates job input, resolves the configured provider, validates output content type and size, creates safe metadata, and returns a `GeneratedFileArtifact` plus `GeneratedAssetDescriptor`. |
| `GenerationJobWorker` and `GeneratedAssetPublisher` | Claim and execute jobs, monitor cancellation, persist outputs, store private files, publish Assets, complete or fail usage accounting, and clean up uncommitted files. |
| `MusicGenerationController` | Authenticates the request, applies CSRF protection, validates workspace and project ownership through the shared job service, runs zero-cost preflight accounting, and creates the `music.generate` job. |

Provider adapters must not write to the database, choose a workspace, publish Assets, or expose raw provider payloads. They return audio content through `MusicProviderResult`. The handler owns content validation and publication descriptors. This keeps future providers replaceable and prevents provider-specific assumptions from entering the browser contract.

## Job lifecycle

The lifecycle is the existing durable Generation Job lifecycle: `Pending`, `Queued`, `Running`, and one terminal state of `Succeeded`, `Failed`, or `Cancelled`.[1] `music.generate` is included in the shared supported job type set. The worker reports progress during validation, provider execution, output validation, and publication. Cancellation is cooperative for running work and immediate for pending or queued work. Music cancellation uses `MUSIC_CANCELLED` for both the job and its usage transaction.

The job input JSON stores only normalized user intent. It does not store credentials, provider responses, storage keys, or raw audio. The normal job DTO intentionally omits provider and model values. Internal provider and model values remain on the server-side Generation Job and usage ledger where existing administrator-only reporting can inspect them.

## Provider status and extension path

The production adapter targets the documented **Mubert AI Music API v3** public track contract. It submits `POST /api/v3/public/tracks` with a bounded text prompt, duration, format, bitrate, intensity, and mode; it polls the documented `GET /api/v3/public/tracks/{track}` resource until a generation URL is available; and it downloads that temporary URL immediately. Mubert generation is asynchronous and its API documentation also describes webhook notifications, so the adapter keeps provider operation status separate from the Taslim Generation Job status and can later replace polling with a verified callback coordinator without changing the browser contract.

The adapter checks `MusicGeneration:Enabled`, the `mubert` provider key, and the server-only `MubertCustomerId` and `MubertAccessToken` values before making any request. It applies the linked job cancellation token, a bounded overall timeout, bounded transient retries, a bounded polling count, a 255-character provider prompt limit, and a bounded streamed output download. Mubert does not document a track-cancellation endpoint in the public v3 contract; cancellation therefore stops Taslim polling cooperatively and never fabricates a remote cancellation request.

The default development and production configuration registers the real adapter but leaves `MusicGeneration:Enabled` false and credentials empty. When enabled, the adapter returns only bounded audio bytes, a supported MIME/format pair, latency, and safe administrator-only metadata. It does not write to the database, select a workspace, publish Assets, or expose raw provider payloads. Mubert’s public documentation does not return authoritative per-generation pricing, so the adapter records no invented provider cost; customer charging remains zero.

The foundation supports `audio/mpeg`, `audio/wav`, `audio/ogg`, `audio/mp4`, `audio/aac`, and `audio/flac`. A provider that returns another format must be rejected until the format is intentionally added to the server allowlist and the browser playback path is tested. The Mubert adapter requests and accepts only `mp3` or `wav` and currently defaults to `mp3`.

## Assets and private storage

Music output uses the existing generated-file publication pipeline.[2] A successful job creates one `StoredFile`, one `GenerationJobOutput`, and one `music` Asset. The Asset stores the logical name, type, workspace, optional project, source job, MIME type, and safe music metadata. The underlying file remains private. Local development uses the configured local storage adapter; production requires the existing S3-compatible private storage configuration and never falls back to ephemeral application disk when that configuration is incomplete.

The Asset controller authorizes the workspace member before opening the private file. Inline requests are allowed for audio content so an authenticated `<audio>` element can play a completed track. Download requests retain the existing attachment behavior. The browser receives an opaque Asset URL and no storage key or storage-provider detail.

## Projects and authorization

The create endpoint accepts an optional project ID. The shared `GenerationJobService` verifies that the authenticated user belongs to the requested workspace and that the project belongs to that workspace. The worker and publisher use the job's server-authorized workspace and project values rather than trusting a browser-provided ownership relationship. Asset listing, playback, download, archive, restore, and metadata operations continue through the existing workspace authorization rules.

## Usage accounting

Music creation uses `UsageFeature.Music`. The controller performs a zero-dollar preflight because no customer charge is active and no production provider pricing is committed in this foundation. The Generation Job usage service creates an idempotent pending transaction with a `generation:{jobId}` request ID. A successful provider adapter may supply estimated or actual internal provider cost metadata, which is stored for administrator-only reporting. Failure and cancellation retain zero customer charge. The shared `SafeUsageChargingService` remains authoritative for the customer amount and returns zero.[3]

Normal users do not see provider, model, pricing, raw provider metadata, or storage details. If a provider is added later, its internal usage data is still limited to the existing server-authorized administration surface.

## Localization and RTL

The `/create/music` experience has English, Arabic, and Kurdish Sorani translation keys. The existing locale provider sets the document language and direction, so Arabic and Kurdish use RTL without a separate Music Studio direction state. Labels, choices, validation messages, progress text, unavailable behavior, result actions, and safety guidance are localized. The layout mirrors the header and result heading in RTL while preserving audio controls and download actions.

## Testing and migrations

Focused API tests cover successful deterministic provider injection, private Asset publication and inline playback, workspace authorization, `UsageFeature.Music`, zero customer charge, bounded validation, and the safe unavailable-provider path. Mubert adapter tests cover documented request construction, polling, transient retry bounds, provider failure, timeout, and cooperative cancellation. Frontend state tests cover job recognition, cancellability, safe result parsing, redaction of provider/model-shaped fields, and malformed result handling.

No database migration is required for this branch. The current domain model already contains `GenerationJobTypes.MusicGenerate`, `AssetTypes.Music`, and `UsageFeature.Music`, and the existing Generation Jobs, Assets, Stored Files, Asset representations, and Usage Transactions tables are sufficient for a single music output. Future provider-specific metadata must remain additive and server-only; it must not add provider secrets or raw payloads to the user-facing schema.

## Deferred work

Provider-specific lyric moderation, waveform generation, loudness normalization, transcoding, stems, multi-track projects, editing, waveform visualization, playlisting, webhook callback coordination, and customer billing remain outside this branch. The Mubert adapter is production-ready at the server boundary but remains disabled until its account credentials and private storage configuration are supplied. Each future feature should preserve the single logical Asset boundary and add a focused representation or related entity only when the product contract requires it.

## References

[1]: ./GENERATION_JOBS_ARCHITECTURE.md "Generation Jobs Architecture"
[2]: ./ASSET_ARCHITECTURE.md "Asset Architecture"
[3]: ./USAGE_ACCOUNTING_ARCHITECTURE.md "Usage Accounting Architecture"
