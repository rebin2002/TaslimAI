# Voice Studio Architecture

Voice Studio is an authenticated speech-generation workflow at `/create/voice`. It accepts text, language, voice style, speaking style, optional instructions, and an optional project. The request becomes a durable `voice.generate` Generation Job. A successful job stores the provider output as a private audio `Asset` and returns user-safe output metadata that the browser can play or download through the existing authenticated asset endpoints.

## Current provider status

The production speech adapter is implemented for OpenAI's Audio Speech endpoint and is **disabled by default**. When enabled with the existing `Ai:OpenAI` credentials, `OpenAiVoiceGenerationProvider` owns provider-specific authentication, request mapping, response parsing, timeout handling, bounded MP3 validation, and safe usage metadata. Provider and model details remain internal to the API; no controller, frontend component, asset publisher, or database entity depends on a provider SDK or provider-specific request shape. The registered `UnconfiguredVoiceGenerationProvider` remains the disabled fallback and fails safely without fabricating audio.

OpenAI's published TTS language list includes English and Arabic but does not include Kurdish Sorani. Sorani remains available in the Voice Studio request contract and UI, but the OpenAI adapter rejects it before making a provider call with `VOICE_LANGUAGE_UNSUPPORTED`; it never claims native Sorani support or silently changes the requested language.

## Request and validation boundary

`VoiceGenerationRequest` is the authenticated HTTP contract. `VoiceGenerationContractMapper` normalizes language and style values before serializing `VoiceGenerationInput` into `GenerationJob.InputJson`. `VoiceGenerationRequestValidator` enforces the configured text, instruction, and title limits and accepts only English (`en`), Arabic (`ar`), and Kurdish Sorani (`ku`).

The public response contains a `GenerationJobDto` without provider or model fields. Provider keys, model keys, cost basis, and provider usage remain internal to the worker and usage ledger. The frontend only receives lifecycle status, safe errors, and output metadata such as format, content type, language, and duration.

## Durable execution lifecycle

The Voice controller checks workspace membership, optional project ownership, and the usage preflight guard before creating the job. `GenerationJobService` creates the job and a pending usage transaction, then queues the job through the existing database-backed queue.

`VoiceGenerationJobHandler` is resolved by the shared `GenerationJobWorker`. It performs the following steps:

1. Deserialize and validate the stored request.
2. Refuse safely when Voice Studio is disabled or the configured adapter is unavailable.
3. Select an adapter by the internal configured provider key.
4. Validate the returned bytes as bounded MP3 `audio/*` content with a supported output format.
5. Build `VoiceOutputMetadata` and a private `GeneratedFileArtifact`.
6. Publish the file and an `audio` Asset through `GeneratedAssetPublisher`.
7. Complete or fail the shared usage transaction.

Cancellation uses the existing cooperative Generation Job cancellation flow. Expired claims are recovered by the shared worker, so the Voice path does not require a second queue or a separate scheduler.

## Storage and authenticated playback

Generated audio is stored through `FileProcessingService.StoreGeneratedAsync`, which uses the configured local or S3-compatible private storage implementation. The database stores only the private storage key and file metadata. The Asset points to the Stored File and to the source Generation Job.

The existing `GET /api/assets/{id}/download` endpoint remains the authorization boundary. The endpoint now permits authenticated inline delivery for `audio/*` content in addition to images. The Voice Studio player uses `?inline=true`; the download action uses the normal attachment response. Both paths require the authenticated session and workspace-level asset authorization.

## Usage accounting

Voice jobs use `UsageFeature.Voice`. OpenAI's speech response is binary and does not currently return authoritative token or cost usage in this adapter, so the ledger records the provider/model internally, stores character/byte counts in safe metadata, and does not invent a provider cost. `SafeUsageChargingService` keeps customer charge at zero. If an authoritative provider usage/cost payload becomes available, it can be passed through `VoiceProviderUsage` without changing the user-facing job contract.

## Localization and RTL

The route is localized in English, Arabic, and Kurdish Sorani. The existing `LocaleProvider` sets document language and direction. Voice controls inherit the document direction, and the page adds RTL-aware header, result, label, and field alignment rules. The selected speech language is independent from the interface locale.

## Provider enablement checklist

The OpenAI adapter is enabled operationally only after configuration is verified:

- Set `Ai:OpenAI:Enabled=true` and provide `Ai:OpenAI:ApiKey` through secret-backed environment configuration, never source control.
- Set `VoiceGeneration:Enabled=true`, `VoiceGeneration:ProviderKey=openai`, and keep the approved model such as `gpt-4o-mini-tts` in deployment configuration.
- Preserve the `UnconfiguredVoiceGenerationProvider` fallback for disabled or incomplete environments.
- Keep the provider contract tests for timeout, authentication/unavailable responses, malformed output, request mapping, and unsupported Kurdish Sorani.
- Add a pricing snapshot only when the provider exposes authoritative usage/cost data or an explicitly approved pricing policy exists.

## Migration status

No database migration is required for this foundation. The current model already contains the required `Voice` usage enum value, generic Generation Jobs, Stored Files, Assets, project relationships, and private storage metadata. Voice adds no new tables or columns.

## References

[1]: ./GENERATION_JOBS_ARCHITECTURE.md "Generation Jobs architecture"
[2]: ./ASSET_ARCHITECTURE.md "Asset architecture"
[3]: ./USAGE_ACCOUNTING_ARCHITECTURE.md "Usage accounting architecture"
