# Image Studio Architecture

## Scope

Batch 3.8 adds the first real user-facing Taslim studio: **Image Studio** at `/create/image`. It is a focused MVP for one generated image per job. The browser collects a structured creative request, the API validates and normalizes it, a durable `GenerationJob` executes asynchronously, and a successful image is stored privately and published as a reusable `Asset`.

The implementation does not add a second queue, a provider-specific database table, public object URLs, image editing, reference-image uploads, or any Movie, Document, Presentation, Voice, Music, Research, or Social studio. The existing Generation Job, StoredFile, Asset, Workspace authorization, CSRF, and Usage Ledger foundations remain the extension points.

## User flow

1. An authenticated user opens `/create/image` from the Media → Images home card or the protected product route.
2. The user enters a description and may select style, aspect ratio, quality, project, title, mood, background direction, and exact text to render.
3. The web client submits the structured request to `POST /api/image-generation/jobs` with the workspace ID and credentials. It does not send provider, model, API key, storage key, or raw prompt-enrichment fields.
4. The API validates the workspace membership, optional project relationship, supported controls, character limits, and the intentionally deferred reference-image field. It creates and queues an `image.generate` Generation Job.
5. The browser polls the existing authorized job snapshot endpoint. It shows progress, safe terminal errors, cooperative cancellation, and no provider/model details.
6. On success, the result points to an authorized Asset. The UI previews the private image through the authenticated download endpoint and links to the Asset Library.

## Provider boundary

`IImageGenerationProvider` is the provider-independent contract. The first registered adapter is `OpenAiImageGenerationProvider`, which uses the server-only `HttpClient` and the OpenAI Images API. The default model is configured server-side as `gpt-image-2.5-sunburst`; it is never selected or named by the browser. The API key is read only from API configuration and is never logged, returned in a DTO, placed in a job result, or passed to the web build.

The adapter sends one request with one image, maps Square/Portrait/Landscape to bounded provider sizes, maps Standard/High to provider quality, requests PNG with an opaque background, and uses standard moderation. The response is accepted only when it contains a valid base64 image with a recognized PNG, JPEG, or WebP signature. HTML, empty bytes, malformed base64, and unrecognized binary output fail safely.

Provider logs contain only provider/model keys, HTTP status, stable safe error codes, request ID when supplied, duration, content type, and output byte count. They do not contain descriptions, enriched prompts, cookies, authorization headers, credentials, base64 output, response bodies, personal memory, or extracted file content.

## Prompt enrichment

`TaslimImagePromptBuilder` turns the structured request into a bounded provider prompt. It preserves the user’s description, exact requested text, named products/people/proper nouns, and explicit creative constraints. It adds the selected visual treatment, aspect composition, quality intent, and safe exclusions against unrelated text, logos, watermarks, or personal-information injection.

The Image Studio does not inject chat history, Personal Memory, or Project Context into image prompts. A selected project is an ownership/storage scope and does not implicitly authorize unrelated project instructions or private memory to be used as generation input.

## Persistence and publication

The job uses the existing `image.generate` type and the existing `GenerationJob` lifecycle. The handler returns a generic `GeneratedFileArtifact` and `GeneratedAssetDescriptor`. `IGeneratedAssetPublisher` then:

1. stores the validated image through the existing private `FileProcessingService` and `IFileStorageService` adapter;
2. creates one `GenerationJobOutput` linked to the new `StoredFile`;
3. creates one `Asset` with type `image`, optional project assignment, image MIME type, safe dimensions/format metadata, and source-job provenance; and
4. lets the worker commit output, Asset, and successful job transition atomically.

If cancellation wins the completion race, the image is discarded and no output or Asset is published. If storage or output validation fails, the worker cleans up the partial private file on a best-effort basis and records a safe terminal error. Asset downloads and previews remain authenticated and workspace-authorized; no public bucket URL is returned.

## Error model

The browser receives stable Taslim codes and generic actionable messages. Examples include `IMAGE_REQUEST_INVALID`, `IMAGE_REFERENCE_NOT_SUPPORTED`, `IMAGE_PROVIDER_UNAVAILABLE`, `IMAGE_SAFETY_REFUSAL`, `IMAGE_OUTPUT_INVALID`, `IMAGE_OUTPUT_STORAGE_FAILED`, and `IMAGE_CANCELLED`. Raw provider error payloads and moderation details are not exposed. A safety refusal tells the user to try a different description without disclosing classifier internals.

## Usage and pricing

Image jobs use the existing `UsageTransaction` ledger with `UsageFeature.Image` and the idempotent `generation:{jobId}` request key. The provider adapter parses returned text/image input and image output token counts when available. Configured GPT Image 2.5 Sunburst rates are kept under `ImageGeneration:Pricing`, and a bounded fallback estimate is used only when the provider omits usage. The existing `SafeUsageChargingService` keeps customer charge at zero; this batch does not activate billing, credits, subscriptions, or payment processing.

## Configuration

Development keeps Image Studio disabled and does not require an OpenAI key. Production enables the Image Studio handler but still requires the existing server-side `Ai__OpenAI__Enabled=true` and `Ai__OpenAI__ApiKey` configuration to make the provider available. `ImageGeneration:ProviderKey`, `Model`, timeout, limits, and pricing are server settings. No web setting, browser secret, new Railway service, queue, Redis instance, bucket, or migration is required for Batch 3.8.

## Testing

The backend test suite uses a deterministic in-memory PNG provider in a relational SQLite host. It covers successful async completion, progress/result/output/Asset persistence, image usage cost with zero customer charge, workspace isolation, validation and deferred reference-image behavior, and safety refusal with no output or Asset. Frontend state tests cover safe result parsing, cancellation eligibility, progress/result display data, malformed results, and provider/model redaction. The complete existing API and frontend suites remain required before publication.

## Deferred work

Reference-image editing is explicitly reserved by `ReferenceFileId` but rejected in this MVP until an authorized StoredFile/Asset input flow is designed. Future image iterations may add reference assets, edits, multiple image outputs, aspect-ratio custom dimensions, transparent backgrounds, provider choice, and richer cost/allowance controls without changing the public Asset destination or the Generation Job lifecycle.
