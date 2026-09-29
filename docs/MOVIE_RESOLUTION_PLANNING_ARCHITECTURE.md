# Movie Resolution Planning Architecture

## Scope

Wave 3 adds a provider-neutral resolution planner for movie delivery tiers:

- 480p
- 720p
- 1080p
- 1440p / 2K
- 2160p / 4K

The planner is a decision layer only. It does not call a video provider, queue a generation job, create an Asset, or enable charging. It is registered as `IMovieResolutionPlanner` so a future Movie Studio workflow can request a plan before any explicit generation action.

## Paths

The planner evaluates two path shapes:

1. **Native** — generate directly at the requested master tier.
2. **Source to master** — use an existing lower-resolution source and create the requested master through a mastering/upscaling step.

A source-to-master path is only considered when the source tier is known and below the target tier. A source tier equal to or above the target is rejected rather than silently discarding the source or pretending that a down-conversion is an upscale.

## Benchmark evidence

`MovieResolutionEvidence` is an adapter-facing, provider/model-neutral evidence record. It contains:

- path kind and target/source tiers;
- availability;
- generation and mastering estimates;
- predicted final-master quality score from 0 to 1;
- benchmark sample count, timestamp, and evidence status.

Provider and model identifiers are intentionally absent from the contract. A future benchmark adapter may keep those details in an internal store and publish only an aggregated path snapshot to the planner.

Evidence is usable only when it is marked `known`, has enough samples, is within the configured freshness window, has non-negative known costs, and has a bounded quality score. Missing or unknown cost is never treated as zero. Missing or stale quality is never treated as a pass.

## Selection policy

The planner selects the lowest total cost among paths whose predicted quality meets the requested minimum. Total cost is the sum of generation and mastering cost. Ties prefer native generation because it has fewer transformation stages. If no path meets the threshold, the response status is `insufficient_evidence`, with alternatives and stable reason codes such as `cost_unknown`, `quality_below_requirement`, `benchmark_sample_insufficient`, or `evidence_stale`.

The response always includes the expected native path and, when a source is supplied, the source-to-master alternative. This makes the absence of evidence visible instead of silently narrowing the decision set.

Source-to-master recommendations include escalation triggers to review the master and fall back to an evidenced native path if mastering QC shows detail loss or other unacceptable artifacts. Recommendations close to the quality threshold also include a quality-review trigger.

## Integration and safety

No database migration is required: benchmark snapshots are not persisted by this foundation. A later persistence/benchmarking workstream can store reviewed snapshots without changing the public plan shape. A future generation integration should:

- revalidate the plan against current evidence before queueing;
- preserve the selected path and evidence version in internal job metadata;
- require explicit user approval for any chargeable generation;
- keep provider/model names and prompts out of normal-user DTOs;
- route actual work through the existing `IMovieVideoProvider`, Generation Job, QC, Asset, and Usage Ledger boundaries.
