# Voice Studio Architecture

Voice Studio is an authenticated speech-generation workflow at `/create/voice`. It accepts text, language, voice style, speaking style, optional instructions, and an optional project. The request becomes a durable `voice.generate` Generation Job. A successful job stores the provider output as a private audio `Asset` and returns user-safe output metadata that the browser can play or download through the existing authenticated asset endpoints.

## Current provider status

The production speech provider is intentionally **not configured** in this branch. `VoiceGenerationOptions.Enabled` defaults to `false`, and the registered `UnconfiguredVoiceGenerationProvider` fails safely without fabricating audio. A user can still submit a validated request and receive a durable job status. The worker marks the job as failed with `VOICE_PROVIDER_UNAVAILABLE`, records zero customer charge, and exposes only the safe unavailable message in the normal Voice Studio UI.

A future provider adapter must implement `IVoiceGenerationProvider`. It owns provider-specific authentication, request mapping, response parsing, timeout handling, and provider usage metadata. No controller, frontend component, asset publisher, or database entity depends on a provider SDK or provider-specific request shape.

## Request and validation boundary

`VoiceGenerationRequest` is the authenticated HTTP contract. `VoiceGenerationContractMapper` normalizes language and style values before serializing `VoiceGenerationInput` into `GenerationJob.InputJson`. `VoiceGenerationRequestValidator` enforces the configured text, instruction, and title limits and accepts only English (`en`), Arabic (`ar`), and Kurdish Sorani (`ku`).

The public response contains a `GenerationJobDto` without provider or model fields. Provider keys, model keys, cost basis, and provider usage remain internal to the worker and usage ledger. The frontend only receives lifecycle status, safe errors, and output metadata such as format, content type, language, and duration.

## Durable execution lifecycle

The Voice controller checks workspace membership, optional project ownership, and the usage preflight guard before creating the job. `GenerationJobService` creates the job and a pending usage transaction, then queues the job through the existing database-backed queue.

`VoiceGenerationJobHandler` is resolved by the shared `GenerationJobWorker`. It performs the following steps:

1. Deserialize and validate the stored request.
2. Refuse safely when Voice Studio is disabled or the configured adapter is unavailable.
3. Select an adapter by the internal configured provider key.
4. Validate the returned bytes as bounded `audio/*` content with a supported output format.
5. Build `VoiceOutputMetadata` and a private `GeneratedFileArtifact`.
6. Publish the file and an `audio` Asset through `GeneratedAssetPublisher`.
7. Complete or fail the shared usage transaction.

Cancellation uses the existing cooperative Generation Job cancellation flow. Expired claims are recovered by the shared worker, so the Voice path does not require a second queue or a separate scheduler.

## Storage and authenticated playback

Generated audio is stored through `FileProcessingService.StoreGeneratedAsync`, which uses the configured local or S3-compatible private storage implementation. The database stores only the private storage key and file metadata. The Asset points to the Stored File and to the source Generation Job.

The existing `GET /api/assets/{id}/download` endpoint remains the authorization boundary. The endpoint now permits authenticated inline delivery for `audio/*` content in addition to images. The Voice Studio player uses `?inline=true`; the download action uses the normal attachment response. Both paths require the authenticated session and workspace-level asset authorization.

## Usage accounting

Voice jobs use `UsageFeature.Voice`. Preflight estimated provider cost is zero until a production adapter and pricing policy are explicitly configured. `SafeUsageChargingService` keeps customer charge at zero. If a future provider reports internal cost metadata, it is retained for operational accounting and is not included in the normal user-facing job contract.

## Localization and RTL

The route is localized in English, Arabic, and Kurdish Sorani. The existing `LocaleProvider` sets document language and direction. Voice controls inherit the document direction, and the page adds RTL-aware header, result, label, and field alignment rules. The selected speech language is independent from the interface locale.

## Provider enablement checklist

A future production adapter should be added without changing the route contract:

- Implement `IVoiceGenerationProvider` in a provider-specific adapter file.
- Register the adapter alongside `UnconfiguredVoiceGenerationProvider`.
- Add secret-backed provider configuration outside source control.
- Set `VoiceGeneration:Enabled` and `VoiceGeneration:ProviderKey` only after configuration is verified.
- Return validated audio bytes, a supported format, and non-sensitive `VoiceProviderUsage` values.
- Add adapter contract tests for timeout, authentication failure, malformed output, and provider response parsing.
- Add a pricing snapshot only when the provider's accounting policy is approved.

## Migration status

No database migration is required for this foundation. The current model already contains the required `Voice` usage enum value, generic Generation Jobs, Stored Files, Assets, project relationships, and private storage metadata. Voice adds no new tables or columns.

## References

[1]: ./GENERATION_JOBS_ARCHITECTURE.md "Generation Jobs architecture"
[2]: ./ASSET_ARCHITECTURE.md "Asset architecture"
[3]: ./USAGE_ACCOUNTING_ARCHITECTURE.md "Usage accounting architecture"
