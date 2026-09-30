# Direct Video Provider Adapter Foundation

## Scope

This foundation provides the reusable server-side seam for future direct video adapters. It does **not** select a production provider, activate a provider, make paid requests, add credentials, change customer charging, or expose provider/model details in normal-user responses.

The existing `IMovieVideoProvider` and movie generation worker remain the current execution boundary. This wave adds a provider-neutral contract beside that boundary so a future direct adapter can normalize its own API without moving vendor concepts into movie jobs, browser DTOs, or persistence models.

## Components

| Component | Responsibility |
|---|---|
| `DirectVideoProviderOptions` | Fail-closed server configuration with bounded timeout, retry, prompt, and output limits. Secrets are read only as an internal configured/not-configured signal. |
| `DirectVideoCapabilityDeclaration` | Declares supported operations, resolutions, aspect ratios, duration range, reference images, continuation, upscaling, and native audio. Unsupported requests fail before an adapter call. |
| `DirectVideoRequestNormalizer` | Converts the existing movie request into a bounded provider-neutral request, canonicalizes resolution, validates safe HTTPS media references, bounds continuity context, and builds the internal prompt text. |
| `DirectVideoResultNormalizer` | Maps provider status strings to a closed status enum and validates output MIME, filename, size, duration, cost metadata, and safe JSON metadata. |
| `DirectVideoErrorNormalizer` | Maps HTTP status and exception types to stable internal categories/codes without copying response bodies, raw provider codes, or exception messages. |
| `IDirectVideoProviderHealthHook` | Optional bounded health hook. Configuration states are reported without making a network call when no probe is supplied. |
| `MockDirectVideoProviderAdapter` | Test-only deterministic adapter covering queued completion, status normalization, private output shape, and health behavior. It is not registered in production. |

## Configuration and activation posture

`DirectVideoProviders` is explicitly disabled and unconfigured in both base and production configuration. The section contains no secret. Binding the options in `Program.cs` only makes the seam injectable; it does not register a direct adapter or change the existing movie provider selection.

A future adapter should be registered only after all of the following are separately approved:

1. Server-only credentials and provider/model configuration are supplied through the deployment secret store.
2. Capability declaration and request mapping pass focused contract tests.
3. Output retrieval copies the provider result into TASLIM private storage before completion.
4. Usage metadata is reconciled through the existing internal ledger and cost guardrails.
5. Commercial, retention, output-rights, quota, and failure-billing gates are complete.
6. The adapter is enabled behind a server-side flag and tested with a no-charge test account or vendor sandbox.

Provider keys, model keys, prompts, output URLs, raw responses, and credentials must remain outside normal-user DTOs and UI state. Safe administrator-only operational summaries may use existing provider-health and usage surfaces.

## Adapter integration shape

A future adapter can implement `IDirectVideoProviderAdapter`:

- `SubmitAsync` accepts only `DirectVideoRequest` and returns an opaque internal operation ID.
- `GetStatusAsync` returns `DirectVideoStatus` after applying `DirectVideoResultNormalizer`.
- `RetrieveAsync` returns a validated `DirectVideoProviderOutput`; the consuming movie handler owns private storage publication.
- `CancelAsync` is cooperative and must not turn a cancellation failure into a user-visible provider detail.
- `Health` exposes an optional cheap authenticated probe through `IDirectVideoProviderHealthHook`.

The adapter may retain provider-specific payloads internally while translating every external failure through `DirectVideoErrorNormalizer`. It must never use a raw provider response body as a job error message, log field, safe metadata value, or browser response.

## Persistence and migrations

No database migration is required. The existing generation-job, movie execution, output, asset, usage, and provider-resilience tables already provide the durable lifecycle. Provider-specific fields should remain server-side and additive if a future benchmark proves a persistence need.

Before activating any real video adapter, retain the existing concurrency-token fencing requirement for movie execution writes and verify stale-worker recovery under the adapter's submit, poll, cancel, and retrieve paths.

## Verification

Focused tests live in `apps/api.Tests/DirectVideoProviderFoundationTests.cs`. They use only synthetic values and the test-only mock adapter; no network request or paid provider call is possible from this foundation.
