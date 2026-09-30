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

## Wave-4 execution bridge

`VideoGenerationAdapterExecutionService` connects the neutral contract to the canonical `IGenerationJobHandler` lifecycle without changing the existing movie handler. It provides a single normalized path for:

1. submit and opaque-handle validation;
2. bounded polling with timeout classification;
3. callback normalization through the same event gate as polling;
4. duplicate callback suppression and stale/regressive event rejection;
5. idempotent cancellation and terminal-state protection;
6. artifact validation before the shared private-storage publisher; and
7. provider-neutral usage evidence enrichment for output bytes and processing time.

`VideoGenerationAdapterJobHandler` is registered as an opt-in handler for canonical movie jobs. The `VideoGenerationAdapters:Enabled` flag is `false` in both base and production configuration, and the production registration is `UnavailableVideoGenerationAdapter`, which performs no network calls. Test hosts can replace that registration with a deterministic fake without activating a real provider.

## Wave-2 compatibility

The repository's existing `IMovieVideoProvider` and `MovieVideoProvider*` records remain in place for the legacy movie execution path, which remains the default while the flag is disabled. The opt-in handler maps the existing canonical `MovieGenerationInput` into `VideoGenerationRequest`, then returns a generic stream-backed output and internal usage evidence to the existing generation-job publisher and ledger. No provider-specific fields are copied to normal-user DTOs.

No provider is enabled or activated by this contract. No real provider SDK, credential, model name, prompt, or paid generation call is added.

## Test coverage

`VideoGenerationAdapterContractTests` uses a deterministic in-memory fake and covers:

- submit → queue → running → succeeded → artifact retrieval;
- explicit capabilities and unsupported-request normalization;
- callback normalization and duplicate callback idempotency;
- cancellation and terminal cancellation behavior;
- artifact/status validation;
- provider/model-independent normalized errors.

`VideoGenerationAdapterExecutionTests` additionally covers the opt-in execution bridge, duplicate and stale event fencing, timeout and best-effort cancellation, fail-closed disabled behavior, and usage-evidence normalization.

## Persistence and integration notes

No database migration is required. The bridge is in-memory at the adapter-session boundary and does not add tables or columns. Existing `GenerationJob`, private file storage, usage ledger, and movie execution persistence remain the durable integration points. A future callback transport should persist the session's event ID and observed-at fence beside its execution row before enabling a real adapter.
