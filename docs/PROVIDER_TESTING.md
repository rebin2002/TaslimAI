# Deterministic Provider and Generation Testing

This repository uses a test-only fake-provider framework to validate the Wave 5 provider and generation contracts without Azure, Stable Audio, Runway, OpenAI, Mubert, or any other paid provider call. The framework lives under `apps/api.Tests/ProviderTesting` and is intentionally not registered by the production application.

## Architecture

`FakeProviderScenarioCatalog` selects a deterministic scenario independently for Image, Voice, Music, and Movie. The corresponding fake provider implements the real Taslim provider interface and returns the same result shapes consumed by the existing handlers. `FakeProviderCallLog` records provider calls, cancellation, and duplicate-result events without relying on wall-clock sleeps or external state.

`ProviderGenerationApiFactory` replaces only provider registrations and uses the existing SQLite test database, generation worker, `IGeneratedAssetPublisher`, local private-storage abstraction, asset persistence, and usage ledger. The API tests therefore exercise the real request-to-job-to-handler-to-validation-to-publication lifecycle. Movie tests seed the minimum valid movie project and clip records, then create the job through the real `IGenerationJobService` before the worker runs the asynchronous movie provider contract.

## Scenario coverage

| Scenario | Framework behavior | Contract or pipeline coverage |
| --- | --- | --- |
| Immediate success | Returns a valid representative output immediately | All four modality output shapes and full lifecycle |
| Asynchronous success | Completes after a deterministic short delay | Music pipeline |
| Queued -> running -> completed | Delays status completion in the movie state machine | Movie provider polling and full lifecycle |
| Transient failure | Raises a typed transient fake failure | Reusable bounded retry helper |
| Permanent failure | Raises a typed non-transient fake failure | Error normalization contract |
| Rate limit | Raises a typed rate-limit failure | Failure lifecycle with no asset or completed ledger |
| Timeout | Raises a typed timeout failure | Failure normalization contract |
| Malformed output | Returns an invalid content type/format | Real handler output validation and no false-success publication |
| Cancellation | Blocks on the supplied token and records cancellation | Provider cancellation contract and voice job cancellation |
| Delayed completion | Defers output until cancellation or completion | Cancellation race coverage |
| Duplicate callback/result | Records a duplicate result event | Idempotent callback/result observability contract |
| Stale worker | Raises a stale-worker failure | Worker/error normalization contract |
| Fallback success | Primary unavailable/rate-limited path invokes fallback | Reusable fallback contract helper |
| All providers unavailable | Raises a safe unavailable failure | Safe failure behavior and sanitization |

Representative outputs are a valid one-pixel PNG for Image, MPEG-like deterministic bytes for Voice, MP3-shaped metadata for Music, and a stream-backed MP4-shaped output for Movie. No output contains provider credentials or vendor-specific secrets.

## Test entry points

`FakeProviderConformanceTests` is the reusable adapter contract suite. Future adapters can use the same inputs and assertions for configuration validation, output shape, cancellation, transient retries, fallback, and secret sanitization.

`ProviderGenerationE2ETests` is the focused API suite. Its success test validates request, job, provider attempt, output validation, private storage, `Asset`, `UsageTransaction`, and final `Succeeded` state for Image, Music, Voice, and Movie. Its failure tests assert that malformed output and rate limiting produce a failed job, no `Asset`, no `GenerationJobOutput`, and a failed usage transaction with zero customer charge. The cancellation test asserts cancelled usage, no asset, and provider cancellation observation.

## CI and migrations

The existing GitHub Actions API test job already executes `dotnet test apps/api.Tests/Taslim.Api.Tests.csproj`, so these deterministic tests run in CI without a new secret, service container, provider credential, network dependency, or workflow change. The suite uses the repository's existing SQLite test factory and local file storage. No database migration, production registration, or production behavior is changed.
