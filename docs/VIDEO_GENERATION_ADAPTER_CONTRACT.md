# Video Generation Adapter Contract

## Purpose

This Wave 3 seam defines the stable boundary for asynchronous video generation without coupling the core domain to a vendor SDK, provider payload, model identifier, prompt format, callback schema, or output URL.

The contract is implemented in [`apps/api/Video/VideoGenerationContracts.cs`](../apps/api/Video/VideoGenerationContracts.cs) and is intentionally internal to the API/worker boundary. Normal-user DTOs must not copy `AdapterKey`, opaque operation handles, provider/model identifiers, prompts, callback headers, raw callback bodies, or safe operational metadata.

## Adapter lifecycle

`IVideoGenerationAdapter` supports:

1. **Capabilities** — operations, aspect ratios, resolutions, duration bounds, output content types, polling/callback support, cancellation, continuation, and output count.
2. **Submission** — a bounded provider-neutral request returns an opaque `VideoGenerationHandle` and an initial normalized status.
3. **Status polling** — adapters map vendor states into `Submitted`, `Queued`, `Running`, `Succeeded`, `Failed`, or `Cancelled`.
4. **Callbacks** — transport-only body/headers enter `NormalizeCallbackAsync`; only a normalized event ID, handle, status, usage evidence, and normalized error leave the adapter.
5. **Cancellation** — cancellation is explicit and idempotent, with `Accepted`, `AlreadyTerminal`, and `NotSupported` outcomes.
6. **Artifact retrieval** — adapters return a bounded stream-backed artifact descriptor. The consumer validates it and copies the bytes into Taslim private storage before publishing an asset.
7. **Usage/cost evidence** — estimated and provider-reported cost are represented by neutral cost basis and normalized measures. Raw billing payloads and provider/model IDs remain restricted operational data.

Adapter failures use `VideoGenerationErrorCode` and `VideoGenerationAdapterException`; raw provider response bodies and exception text never cross the boundary.

## Wave-2 compatibility

The repository's existing `IMovieVideoProvider` and `MovieVideoProvider*` records remain in place for the Wave-2 movie execution path. This branch does not rewrite that live flow or merge unrelated Wave-2 work. The new `Taslim.Api.Video` contract is a clean compatibility seam for the next movie/video handler migration: an adapter can map the existing movie request context into `VideoGenerationRequest`, then map neutral status/artifact/usage results into existing job persistence until that consumer is migrated.

No provider is enabled or activated by this contract. No real provider SDK, credential, model name, prompt, or paid generation call is added.

## Test coverage

`VideoGenerationAdapterContractTests` uses a deterministic in-memory fake and covers:

- submit → queue → running → succeeded → artifact retrieval;
- explicit capabilities and unsupported-request normalization;
- callback normalization and duplicate callback idempotency;
- cancellation and terminal cancellation behavior;
- artifact/status validation;
- provider/model-independent normalized errors.

## Persistence and integration notes

No database migration is required. The contract is in-memory and does not add tables or columns. Existing `GenerationJob`, private file storage, usage ledger, and movie execution persistence remain the integration points for a future consumer.
