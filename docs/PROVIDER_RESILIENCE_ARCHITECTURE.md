# Provider Resilience Foundation

## Scope

Wave 5 Task 7 adds an internal, provider-neutral resilience boundary. It does not register, select, or activate Azure, Stable Audio, Runway, or any other new provider. Existing Taslim user contracts remain provider-agnostic; a future handler can call `IProviderResilienceOrchestrator<TInput, TOutput>` with a bounded ordered list of capability-aware adapters.

The public failure contract is intentionally generic:

> Taslim could not complete the request.

Provider keys, model identifiers, raw exception text, credentials, response bodies, and vendor URLs remain internal.

## Architecture

`ProviderResilienceOrchestrator` owns the attempt lifecycle. `IResilientProvider<TInput,TOutput>` is the adapter contract. `IProviderResilienceStore` is the durability boundary, implemented by `EfProviderResilienceStore` over the existing PostgreSQL-backed `TaslimDbContext`. `IProviderCostGuard` is an integration point for the existing usage and credit controls; the default foundation implementation allows attempts and does not change billing.

The execution order is:

1. Claim the job execution using `(GenerationJobId, JobConcurrencyToken, IdempotencyKey)`. A completed claim is replayed without calling a provider; an active claim is not duplicated.
2. Bound the provider list to `ProviderResilience:MaxProviders`.
3. Skip adapters that cannot handle the requested capability.
4. Check the durable circuit for the provider/capability pair.
5. Check cancellation and cumulative estimated cost before every attempt.
6. Create a sanitized durable attempt row, execute with a bounded timeout, and complete the row with category, latency, retry/fallback flags, and optional estimated cost.
7. Retry only the retryable categories for the current provider.
8. Open or recover the provider circuit from the durable state store.
9. Move to the next capability-compatible provider only when the current result permits fallback.
10. Complete finalization exactly once, fenced by the current generation-job concurrency token.

No output publication or billing change is performed by this foundation. A consuming handler remains responsible for validating and publishing its output before completing the existing generation job.

## Durable state

The additive `AddProviderResilienceFoundation` migration creates three tables.

| Table | Purpose | Important safeguards |
|---|---|---|
| `ProviderAttempts` | One sanitized record per provider attempt | Job/attempt correlation index, provider/capability/time index, no prompts or secrets |
| `ProviderCircuits` | Shared circuit state across API/worker instances | Unique provider/capability key, open/probe timestamps, concurrency token (`RowVersion`) |
| `ProviderExecutionFinalizations` | Cross-instance finalization claim and replay fence | Unique job/idempotency key, claim lease, completed state |

`GenerationJob.ConcurrencyToken` and the existing running-job claim lease remain the authoritative worker fence. The EF store checks that the job is still `Running` and that the supplied token is current before starting or completing an attempt and before completing finalization. A recovered worker therefore receives `StaleProviderWorkerException` and cannot write provider progress after a new worker owns the job.

Circuit state is shared through PostgreSQL rather than process memory. `Closed` admits normally. `Open` rejects until `OpenUntil`. The first request after that timestamp acquires a single `HalfOpen` probe lease; other requests receive `ProbeInProgress`. A successful probe closes and resets the circuit. A failed probe reopens it. EF's `RowVersion` is configured as a concurrency token so concurrent state transitions fail rather than silently overwriting one another.

## Lifecycle and retry rules

The internal result categories are:

- `Success`
- `Unavailable`
- `RateLimited`
- `TransientFailure`
- `PermanentFailure`
- `UnsupportedCapability`
- `ValidationRejected`
- `InvalidUserInput`
- `Cancelled`
- `TimedOut`
- `CircuitOpen`
- `CostGuardRejected`

The default policy is at most three providers, three attempts per provider (initial attempt plus two retries), a 180-second provider timeout, exponential backoff starting at 250 ms and capped at 4 seconds, and a three-failure circuit threshold with a 30-second open interval. All values are configuration-bound and have bounded defaults.

| Category | Retry same provider | Try next compatible provider | Circuit failure signal |
|---|---:|---:|---:|
| Success | No | No | Reset/close |
| Unavailable | No | Yes | Yes |
| Rate limited | Yes, bounded | Yes after retry budget | Yes |
| Transient failure | Yes, bounded | Yes after retry budget | Yes |
| Timed out | Yes, bounded | Yes after retry budget | Yes |
| Permanent failure | No | Yes, if adapter policy allows | Yes |
| Unsupported capability | No | Yes, capability-aware | No provider-health penalty |
| Circuit open | No | Yes | Already open |
| Invalid user input | No | No | No |
| Validation rejected | No | No | No |
| Cancelled | No | No | No |
| Cost guard rejected | No | No | No |

Cancellation is checked before every attempt and between providers. A cancellation request stops fallback immediately. Explicit user input and deterministic validation failures never cause a retry or fallback chain.

## Fallback rules

Fallback is internal. The user does not receive a provider-by-provider explanation. Providers are supplied in a deterministic preference order by the future consuming handler; the orchestrator caps the list and never follows an unbounded chain. An adapter must return `CanHandle == false` for unsupported capabilities, which allows the next compatible adapter without treating the current provider as unhealthy.

Only one finalization claim exists for a job/idempotency key. A successful fallback completes that claim once. A replay of a completed claim returns a replay result without invoking any provider. The cumulative cost is read from persisted attempts and passed to `IProviderCostGuard` before every attempt; a future usage/credit implementation can reject the next attempt without changing orchestration or billing semantics.

## Observability

Attempt and circuit records contain only sanitized operational data:

- provider key and capability key;
- attempt number, retry flag, and fallback flag;
- result category and safe error code;
- latency in milliseconds;
- optional estimated cost;
- circuit state and timestamps;
- generation-job and concurrency-token correlation identifiers.

Logs use provider key, capability, attempt number, category, and exception type only. They never log API keys, authorization headers, prompts, generated bytes, raw provider payloads, or stack traces in user-visible responses.

## Test coverage

`ProviderResilienceTests` simulates the required cases without activating an external provider:

- success;
- transient retry;
- permanent failure without retry;
- rate limiting;
- timeout;
- circuit opening and half-open recovery;
- fallback success;
- all providers unavailable;
- cancellation during fallback;
- stale worker fencing;
- idempotent replay.

The focused test run is:

```text
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --no-build --filter FullyQualifiedName~ProviderResilienceTests
```

## Migration and activation status

Migration: `20260925111415_AddProviderResilienceFoundation` (the exact timestamp prefix is generated by EF in this branch). The migration is additive and does not alter billing tables, provider credentials, provider enablement flags, or Railway configuration.

The API registers only the internal resilience store, orchestrator, cost-guard integration point, and system time provider. No provider adapter is registered through this foundation, and no deployment or provider activation is performed.
