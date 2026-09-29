# Provider-neutral upscaling foundation

## Scope

Wave 3 adds a durable upscaling job/domain/service seam for a selected Taslim `Asset` at **1080p**, **2K**, or **4K**. It is persistence- and contract-ready, but intentionally does **not** call, configure, benchmark, or activate a paid upscaling provider. The default adapter is explicit and unavailable, so the foundation cannot create a successful-looking output without a separately reviewed provider implementation.

Provider keys, model identifiers, prompts, execution references, and raw provider payloads are not part of the normal upscaling DTOs. The user-facing API exposes only the selected source asset, target resolution, job lifecycle, safe error state, retry eligibility, output asset, and quality-review handoff.

## Domain and persistence

The additive migration `20260929213223_AddUpscalingJobs` creates:

| Entity | Purpose |
| --- | --- |
| `UpscalingJob` | Workspace-scoped request, target resolution, source/output asset links, lifecycle, idempotency fingerprint, retry counters, and bounded provenance JSON |
| `UpscalingAttempt` | Append-oriented per-attempt idempotency key, attempt number, safe failure classification, and retryability |
| `UpscalingQualityHandoff` | Source/output linkage and reviewer decision after processing, preserving a QC boundary before terminal completion |

The source asset must be an active image or video asset with a ready private `StoredFile`. Source provenance captures only bounded Taslim-owned identifiers and media facts. Output provenance is written by a future execution adapter after it has produced and published a private Asset through the existing Asset/StoredFile boundaries.

## Lifecycle

```text
Pending → Queued → Running → QualityControlPending → Completed
   │         │         │                 │
   └─────────┴─────────┴→ Cancelled      └→ QualityControlRejected → Queued
                         └→ Failed → Queued (only when the attempt is retryable and retry budget remains)
```

- Initial creation is `Pending`; no worker is registered for upscaling on this branch.
- `BeginExecutionAsync` creates a stable attempt idempotency key and fences the job in `Running`.
- `CompleteAsync` requires the current attempt and a workspace-owned image/video output Asset, then creates a pending QC handoff.
- QC approval moves the job to terminal `Completed`; rejection preserves the handoff and makes the job retryable.
- `FailAsync` records a safe code and whether a retry is allowed. Permanent failures cannot be retried.
- `RetryAsync` increments the retry counter, clears the prior output pointer, and returns the job to `Queued` without deleting provenance or attempts.
- The filtered unique `(CreatedByUserId, IdempotencyKey)` index prevents duplicate requests. A request fingerprint rejects reuse of a key for a materially different source, target, project, or title.

## Provider boundary

`IUpscalingProvider` is intentionally generic:

```csharp
public interface IUpscalingProvider
{
    string Key { get; }
    bool IsAvailable { get; }
    IReadOnlySet<string> SupportedResolutions { get; }
    Task<UpscalingSubmission> SubmitAsync(UpscalingProviderRequest request, CancellationToken cancellationToken = default);
    Task<UpscalingProviderStatus> GetStatusAsync(string executionReference, CancellationToken cancellationToken = default);
    Task<UpscalingProviderOutput> RetrieveAsync(string executionReference, UpscalingProviderStatus status, CancellationToken cancellationToken = default);
    Task CancelAsync(string executionReference, CancellationToken cancellationToken = default);
}
```

Only `UnavailableUpscalingProvider` is registered. A future adapter must validate target capability, preserve the job attempt idempotency key, publish output through Taslim private storage, and call the service lifecycle methods; it must not bypass Asset provenance, QC, authorization, or usage guardrails.

## API surface

All routes require authentication, workspace membership, and antiforgery protection for mutations:

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/upscaling/jobs` | Persist a source-asset upscaling request with idempotency support |
| `GET` | `/api/upscaling/jobs/{id}` | Read an authorized user-safe job snapshot |
| `GET` | `/api/upscaling/jobs` | List jobs by workspace, status, target, and bounded pagination |
| `POST` | `/api/upscaling/jobs/{id}/retry` | Requeue an eligible failed, QC-rejected, or cancelled job |
| `POST` | `/api/upscaling/jobs/{id}/cancel` | Cancel a pending or queued job |
| `POST` | `/api/upscaling/jobs/{id}/quality-review` | Approve or reject a pending QC handoff |

No route exposes provider/model names, prompts, provider URLs, execution references, storage keys, or internal attempt rows.

## Integration and conflict notes

- The change is additive and does not alter the existing Generation Job, Movie Studio, provider resilience, usage ledger, billing, or customer charging behavior.
- A future execution branch should register its adapter in place of `UnavailableUpscalingProvider` and add a worker/handler only after provider readiness, cost, safety, and QC review.
- The EF model snapshot and migration are generated from the current `origin/main` model. Concurrent EF changes should be reconciled in `TaslimDbContext.cs` and `TaslimDbContextModelSnapshot.cs` rather than regenerated blindly.
- The current implementation intentionally has no customer charge, provider call, provider credential, or deployment change.
