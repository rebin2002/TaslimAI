# Provider Benchmarking Foundation

## Scope

This foundation adds durable, provider-neutral storage for empirical movie-shot benchmark observations. It does **not** execute a provider, register a new provider, select a preferred provider, enable billing, or add a browser/API route.

The benchmark service is an internal seam for a future controlled harness. The harness can record multiple provider/model routes against the same normalized shot fixture and compare observations after the run has completed.

## Durable records

The `AddProviderBenchmarkingFoundation` migration creates four tables:

| Table | Purpose |
|---|---|
| `ProviderBenchmarkRuns` | One benchmark definition/fixture execution, with an idempotent run key, lifecycle status, harness metadata, timestamps, and provenance. |
| `ProviderBenchmarkScenarios` | A normalized shot fixture: shot type, camera motion, aspect ratio, target resolution, duration, subject/reference counts, continuity/audio/dialogue flags, and extensible characteristics JSON. |
| `ProviderBenchmarkMeasurements` | One observation for a scenario and internal provider/model route, including outcome, retry/attempt counts, latency phases, output dimensions/size, cost observations, quality scores, metrics, timestamps, and provenance. |
| `ProviderBenchmarkEvidence` | Rerunnable evidence attached to a measurement, such as an output hash, evaluator report, review record, opaque internal reference, capture timestamp, and provenance. |

Raw generation prompts are not part of the benchmark contract. A scenario stores characteristics and a fixture revision/hash instead, which keeps the benchmark catalog stable and prevents provider request shapes from leaking into the persistence boundary.

## Rerun and idempotency rules

- `RunKey` is unique. `GetOrCreateRunAsync` returns the existing run when the same key and benchmark definition are replayed.
- A scenario is unique within a run by `(RunId, ScenarioKey)`. Re-recording the same scenario updates its normalized characteristics rather than creating a duplicate.
- A measurement is unique within a run by `(RunId, MeasurementKey)`. Re-recording the same key updates the observation, while changing its scenario/provider/model association is rejected.
- Evidence is unique per measurement by `(MeasurementId, EvidenceKey)` and can be updated by the harness when a capture is retried.
- Completed, failed, or cancelled runs are immutable through the write methods. A fresh benchmark execution uses a fresh run key.
- Unique-key races are re-read after a database write conflict so concurrent harness retries converge on the same record.

## Comparison contract

`GetAggregatesAsync` groups observations by scenario key, characteristics hash, shot type, provider key, and model key. It reports sample count, success count/rate, average and p50 total latency, and available average scores/cost.

The aggregate has no `Winner`, `PreferredProvider`, or permanent ranking field. Provider/model selection remains an explicit, separately governed decision based on a dated benchmark run, evidence, commercial approval, and current product requirements. The service is an observation ledger, not a router.

Provider and model identifiers are server-side benchmark data. No user-facing DTO or UI contract was added in this change.

## Activation and safety status

- No benchmark executor or provider adapter was added.
- No provider credentials, model configuration, pricing enablement, or feature flag was changed.
- No paid generation request is made by the service or tests.
- Tests use SQLite and synthetic observations only.
- The service is registered as a scoped internal dependency so a future harness can be added without changing the movie provider abstraction.

## Integration seam

A future harness should:

1. Create or resume a run with a stable `RunKey`, definition key, fixture manifest hash, harness version, environment fingerprint, and provenance JSON.
2. Upsert normalized scenarios using fixture revision and characteristics hash; do not persist raw prompts.
3. Execute only through an explicitly approved, separately gated provider adapter and record each observation with a deterministic measurement key.
4. Attach output hashes, evaluator results, or review records as evidence using stable evidence keys.
5. Complete the run with a terminal status and completion code.
6. Read aggregates for analysis; do not encode a permanent provider winner in the service.

The current branch stops before step 3 by design.
