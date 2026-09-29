# Movie Wave 3 — Shot-Type Provider Performance Intelligence

## Scope

This change adds a pure, provider-neutral scoring seam for ranking opaque generation candidates against historical benchmark evidence for these observable shot requirements:

- close-up / face
- subject motion
- camera movement
- hands / body
- environment
- continuity
- VFX
- text / signage
- lip-sync

The scorer lives in [`apps/api/Movies/MovieShotProviderIntelligence.cs`](../apps/api/Movies/MovieShotProviderIntelligence.cs). It accepts normalized shot requirements and historical aggregate evidence supplied by an internal caller. It does not call a provider, read credentials, estimate cost, choose a vendor/model, create a generation job, or change customer charging.

## Evidence provenance

The contract intentionally has three distinct result states:

| State | Meaning | Score behavior |
| --- | --- | --- |
| `Empirical` | An aggregate historical benchmark record exists for the candidate and shot type | Returns the recorded 0–1 suitability and confidence, sample count, benchmark version, and evidence reference |
| `Fallback` | The caller supplied an explicit cold-start fallback policy | Returns the fallback score and confidence, with no sample count or empirical reference |
| `Mixed` | Some requested dimensions are empirical while others are fallback or unknown | Returns the weighted known score with coverage and keeps each dimension's provenance separate |
| `Unknown` | Neither empirical nor explicit fallback evidence exists | Returns a `null` score and zero confidence; it is never converted to a zero-suitability score |

Candidate confidence is weighted by requested shot-type importance and reduced by evidence coverage when some dimensions are unknown. Fallback candidates are kept separate from fully empirical candidates during deterministic ranking, so a fallback value cannot masquerade as a benchmark result.

## Compatibility seam

The current `main` branch does not contain the Wave 2 adaptive-resolution/shot-quality contracts. This branch therefore uses the small `MovieShotBenchmarkRequirement` record rather than importing unmerged Wave 2 types. When Wave 2 is integrated, its normalized quality requirements can be mapped into this record without changing benchmark storage, evidence provenance, or ranking behavior. No Wave 2 branch was merged here.

A future internal resolver may map an opaque candidate key to provider/model configuration after ranking. That mapping must remain server-side. The benchmark contract has no provider/model fields and is not a normal-user DTO.

## Persistence and migration status

No database migration is included. Historical evidence is supplied as an internal aggregate input so this wave can be integrated with an approved benchmark store later without inventing a schema or presenting unverified static data as empirical. Unknown results are the safe default when the store has no record.
