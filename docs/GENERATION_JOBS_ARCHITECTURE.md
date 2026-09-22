# Generation Job Foundation

Batch 3.6 establishes a provider-independent execution foundation for Taslim’s future long-running workflows. It does **not** implement Image Studio, Movie Studio, Document Studio, Presentation Studio, Voice, Music, Research, or Social providers. The only registered job type is the deterministic internal `system.test` handler.

## Domain and lifecycle

`GenerationJob` is a workspace-scoped, GUID-keyed record owned by the creating user. It stores bounded input and result JSON, optional project association, safe error codes/messages, progress, cancellation intent, and UTC lifecycle timestamps. Provider and model fields are reserved for future internal execution metadata and are not returned by the normal job API or displayed by the validation UI.

The normal state path is:

```text
Pending → Queued → Running → Succeeded
                    ├──────→ Failed
                    └──────→ Cancelled
```

A job is created as `Pending`, then the database-backed queue transitions it to `Queued`. A worker atomically claims a queued row and changes it to `Running`. Successful handlers store bounded result JSON and outputs, set progress to 100, and complete the job. Handler failures become `Failed` with `JOB_EXECUTION_FAILED` or another safe machine-readable code. Pending and queued jobs can be cancelled immediately. Running jobs set `CancellationRequested` and receive a cooperative cancellation token; the handler is responsible for honoring it. Terminal jobs cannot be cancelled again and return a conflict response.

## Queue and worker

`IGenerationJobQueue` is the replaceable application boundary. Batch 3.6 implements it with PostgreSQL-backed state rather than Redis, SQS, RabbitMQ, or an in-memory queue. The database is the durable source of truth, so a process restart does not lose a job.

`GenerationJobWorker` is an ASP.NET Core hosted service. Its default concurrency is one and it can be configured through `GenerationJobs:WorkerConcurrency`. On PostgreSQL, claiming uses a transaction, `FOR UPDATE SKIP LOCKED`, and an ordered status/queue index. This allows multiple API replicas to poll without claiming the same queued row. The SQLite test path uses a conditional `UPDATE ... WHERE Status = Queued` inside a transaction so duplicate claims are rejected in relational integration tests. Individual handler failures are logged with the job identifier and safe type metadata, transitioned to a sanitized failed state, and do not stop the worker loop.

Progress updates use independent scoped database contexts. Cancellation monitoring also uses a separate scope so handler execution, progress writes, and cancellation reads do not share a concurrently used `DbContext`.

## Handlers

Handlers implement `IGenerationJobHandler` and are independently registered. The worker discovers a handler through `CanHandle(jobType)` rather than a future-studio switch statement. `SystemTestGenerationJobHandler` performs five short asynchronous stages, reports visible progress, produces deterministic JSON, and creates a JSON output association without storing a binary file. Future handlers can use the same contract to link `GenerationJobOutput.StoredFileId` to an existing `StoredFile`; the job system does not duplicate R2 or local storage behavior.

## API and authorization

The authenticated endpoints are:

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/generation/jobs` | Validate workspace/project/type, create, record zero-cost usage, and enqueue a job |
| GET | `/api/generation/jobs/{id}` | Return a safe job snapshot for an authorized workspace member |
| GET | `/api/generation/jobs` | List workspace jobs with page, pageSize, status, projectId, and jobType filters |
| POST | `/api/generation/jobs/{id}/cancel` | Cancel a pending/queued job or request cooperative cancellation for a running job |

The service, not the controller, owns membership checks, project/workspace validation, supported type validation, lifecycle changes, and queue calls. Cross-workspace reads return a safe not-found result; workspace list operations return authorization failure. Request bodies, provider fields, model fields, stack traces, credentials, and raw exception details are never returned in normal DTOs.

## Outputs and storage

A job may have zero, one, or many `GenerationJobOutput` rows. Each output has a generic output type, optional metadata JSON, and an optional foreign key to `StoredFile`. The foreign key is restrictive so job domain code cannot silently delete a stored file. Generated files must use the existing `StoredFile` and `IFileStorageService` architecture; Batch 3.6 creates no new storage adapter.

## Usage extension points

Job creation records a `UsageTransaction` with the existing ledger using the new `Generation` feature and an idempotent `generation:{jobId}` request key. The `system.test` handler finalizes with provider cost and customer charge equal to zero. Failures and cancellations finalize the transaction as failed with zero charge. `IGenerationJobUsageService` is intentionally small: future handlers can add allowance reservation, provider-cost recording, customer-usage finalization, refund, or reversal behavior without changing the job controller or domain model. No image, movie, or other studio pricing is introduced.

## Protected validation UI

The protected `/account/generation-jobs` page is an internal/development validation surface, not a studio. It creates only `system.test`, polls by job ID, shows status/progress/result, permits eligible cancellation, and lists recent jobs in the current workspace. It deliberately omits provider, model, storage, infrastructure, and internal exception details. Strings are localized in English, Arabic, and Kurdish Sorani, and the additive CSS preserves RTL behavior.

## Migration and operations

`AddGenerationJobs` creates the two job tables, restrictive workspace/user/project foreign keys, output relationships, and indexes for status/queue ordering, workspace/created time, workspace/status/created time, project, and output lookups. It is additive and coexists with `AddDataProtectionKeys`; the `IDataProtectionKeyContext`, `DataProtectionKeys` DbSet, `SetApplicationName("Taslim.Api")`, and PostgreSQL-backed Data Protection persistence remain unchanged.

Production startup continues to use the existing advisory-lock migration runner. No new infrastructure, Railway variable, provider secret, or external queue is required. The worker runs by default in non-testing environments. When no job is available it waits one second by default before polling again; consecutive jobs are drained immediately. Expired-claim recovery runs immediately at worker start and then every 30 seconds, independently of idle queue polling. These values can be tuned through `GenerationJobs:WorkerConcurrency`, `GenerationJobs:PollIntervalMilliseconds`, `GenerationJobs:CancellationPollMilliseconds`, and `GenerationJobs:ClaimRecoveryIntervalMilliseconds`.

Realtime status transport is intentionally deferred. The API snapshot and polling contract can later support SignalR, WebSockets, or SSE without changing the job lifecycle or handler boundaries.
