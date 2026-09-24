# Taslim.AI Production Operations Runbook

## Purpose and operating boundary

This runbook describes the read-only diagnostics available for Taslim.AI production operations. It is intended for incidents after launch, especially generation failures, delayed jobs, private-storage failures, provider outages, database problems, and payment-foundation errors.

**Customer charging remains disabled.** Payment code records and diagnoses lifecycle state, but it does not activate a payment provider or submit a charge. Optional generation providers are not activated by observability code. The runbook does not authorize destructive database or administrative actions.

The API uses the existing durable generation-job, usage-ledger, asset, storage, payment-foundation, and Admin Operations architecture. It does not introduce a second monitoring system.

## Structured logs

The API uses the ASP.NET Core `ILogger` pipeline configured in `appsettings.json`. Production log sinks can serialize the named properties in each message. Operators should search by the event message and then filter by the structured properties rather than parsing message text.

Important event families include the following:

| Event family | Important properties | What it answers |
| --- | --- | --- |
| Request and authentication | `RequestId`, `HttpMethod`, `RequestPath`, `Authenticated`, `Reason`, `ErrorType` | Which request failed, and whether the failure was validation, authentication, lockout, or antiforgery related? |
| Generation lifecycle | `JobId`, `WorkspaceId`, `JobType`, `RequestId`, `RetryCount`, `ClaimExpiresAt`, `ElapsedMs`, `UsageFinalized` | Was the job created, claimed, recovered, completed, cancelled, or failed? |
| Provider execution | `ProviderKey`, `ProviderModel`, `ProviderFailureCategory`, `ProviderHttpStatus`, `ProviderErrorCode`, `FailureCode`, `ElapsedMs` | Did the provider reject, time out, rate-limit, or fail the operation? |
| Lease recovery | `JobId`, `RequestId`, `RetryReason`, `RetryCount` | Was a running job returned to the queue because its worker lease expired? |
| File storage | `FileId`, `WorkspaceId`, `StorageProvider`, `Extension`, `SizeBytes`, `FailureCategory` | Did upload, publication, cleanup, or extraction fail? |
| Payment foundation | `CheckoutSessionId`, `PaymentAttemptId`, `PaymentEventId`, `PaymentRefundId`, `ReconciliationId`, `ProviderKey`, `EventType`, `Status` | Was a payment or webhook state transition recorded, rejected, replayed, or reconciled? |
| Unexpected API errors | `RequestId`, `Method`, `Path`, `ExceptionType` | Which unhandled server exception produced the safe 500 response? |

Logs deliberately omit passwords, access tokens, API keys, cookies, CSRF tokens, payment secrets, raw webhook payloads, private document contents, full prompts, and storage keys. Provider request IDs may be logged when the provider adapter already exposes them as safe diagnostics; credentials and payloads are never logged.

## Request correlation

Every request receives a safe `X-Request-ID` response header. A caller-supplied value is accepted only when it is at most 128 characters and contains letters, numbers, `-`, `_`, or `.`. Invalid or oversized values are replaced with a generated identifier. The same value is assigned to `HttpContext.TraceIdentifier` and is included in the request logging scope.

Generation jobs persist the request ID internally. The customer-facing generation-job DTO does not expose it. A production diagnosis can therefore follow this path without adding internal identifiers to normal user responses:

```text
X-Request-ID
  -> GenerationJob.RequestId + GenerationJob.Id
  -> provider and storage log events for the job
  -> Asset.SourceGenerationJobId / GenerationJobOutput
  -> UsageTransaction.GenerationJobId and RequestId= generation:<job-id>
```

For a request that creates a job through a studio-specific service, the job service reads the current request trace identifier when no explicit value is supplied. Background work continues to use the persisted job ID and request ID after the HTTP request has ended.

## Health and readiness

The API separates process liveness from dependency readiness.

| Endpoint | Meaning | Failure behavior |
| --- | --- | --- |
| `/health` | Backward-compatible liveness endpoint | Returns HTTP 200 when the process can answer. |
| `/health/live` | Process liveness | Returns HTTP 200 with `status=alive`; it does not query dependencies. |
| `/health/ready` | Application readiness | Returns HTTP 200 with `status=ready` only when the database is reachable and required storage is available. Otherwise it returns HTTP 503 with safe check names and statuses. |

Readiness checks the database with a connectivity probe. It checks the configured storage adapter with a read-only existence request for a reserved health prefix. It does not write a sentinel object and it never returns an R2 endpoint, bucket, credential, or key.

Optional providers are intentionally absent from readiness. A disabled or unconfigured provider must not prevent the API from serving authentication, existing assets, or other available studios. Provider health is reported separately as `disabled`, `unconfigured`, `available_unknown`, or `recent_operational_failure`; `available_unknown` means that configuration is present but no paid health-generation call was made.

## Admin Operations dashboard

`GET /api/admin/operations/dashboard` is protected by the existing `TaslimAdministrator` policy. Normal users, anonymous callers, and disabled administrators must not receive the dashboard. The endpoint remains read-only and exposes no secrets, storage keys, raw prompts, email addresses, or provider payment references.

The dashboard now adds the following operational signals to the existing aggregates:

- queued and pending job count;
- running jobs with retry count, claim expiry, and a fifteen-minute long-running flag;
- total durable retry count in the selected range;
- stored-file status and extraction-status breakdowns;
- failed storage and extraction counts in the selected range;
- payment-event status and rejected-webhook count;
- pending or mismatched reconciliation count; and
- conservative provider health classifications with the most recent safe failure code and time.

No dashboard action deletes data, retries a job destructively, changes provider configuration, activates billing, or reveals secrets.

## Generation failure diagnosis

Start with the `JobId` from the internal log line or the protected dashboard. Confirm the terminal state in `GenerationJobs` and record the safe `ErrorCode`. Then search the logs for the same `JobId` and `RequestId`.

A job in `Queued` or `Pending` with no claim event usually indicates that the worker is disabled, the service is not ready, or the database queue query is failing. A `Running` job whose `ClaimExpiresAt` is in the past should produce an expired-lease recovery event. Check `RetryCount`, worker iteration errors, and the deployment instance logs before taking any manual action.

A long-running job should be compared with its `JobType`, provider category, start time, and claim expiry. Do not infer failure solely from duration. Provider calls, document rendering, video polling, and object publication have different expected durations.

For a failed job, use this order:

1. Read the safe `ErrorCode` and identify whether it is validation, provider, storage, extraction, rendering, cancellation, or publication related.
2. Search the correlated logs for `ProviderFailureCategory`, HTTP status, provider error code, storage provider, and elapsed time.
3. Check whether a usage transaction exists for `GenerationJobId`. Its state should be `Completed`, `Failed`, or `Cancelled`; a remaining `Pending` transaction is a finalization anomaly.
4. Check `GenerationJobOutput`, `Asset`, and `StoredFile` rows. Failed or cancelled jobs should not expose a customer asset.
5. If the error is transient, allow the existing worker lease-recovery behavior or the documented application retry path to operate. Do not create a second job without checking idempotency and usage state.

The user-facing job response contains only safe status, failure code, and safe failure message. Provider diagnostics remain in logs and the protected admin view.

## Storage and extraction diagnosis

For a storage failure, search by `FileId`, `WorkspaceId`, and `StorageProvider`. The log should identify the operation category without exposing the storage key. Check `/health/ready` first, then confirm the storage provider configuration in Railway. For R2/S3-compatible storage, verify the endpoint is HTTPS, the region and bucket are correct, and the service has the required object permissions. Do not print or paste access keys into logs or incident notes.

A generated publication failure normally leaves the job failed and the temporary publication discarded. An upload failure leaves the `StoredFile` in `Failed` state. An extraction failure is recorded separately through `TextExtractionStatus=Failed` and a safe extraction failure code. The original file may still be available when storage succeeded but extraction failed; inspect the file status before deciding whether the user needs to upload again.

The local storage adapter is suitable for development and test environments. Production should use the configured private S3-compatible provider and verify that readiness reports `storage=available` before accepting generation traffic.

## Database diagnosis

If `/health/ready` reports `database=unavailable`, inspect the API deployment logs and managed PostgreSQL status before changing application settings. Confirm that the Railway `ConnectionStrings__Postgres` reference resolves to the intended database and that the service is using the expected environment.

Migration application is controlled by `Database:ApplyMigrations`. Production migration startup logs identify migration failures without returning connection strings. The observability migration adds nullable `GenerationJobs.RequestId` and a non-null `GenerationJobs.RetryCount` with a zero default, so existing jobs remain valid. Apply migrations through the existing deployment workflow; do not run development reset or database recreation commands against production.

If database connectivity is intermittent, compare the timestamps of readiness failures, worker iteration failures, payment webhook failures, and usage finalization failures. A single request can be retried safely only after checking idempotency keys and existing durable rows.

## Provider diagnosis

The admin dashboard distinguishes configuration from observed operation. `disabled` means the feature flag is off. `unconfigured` means the feature is enabled or expected but its provider credentials or provider adapter are not available. `available_unknown` means configuration and adapter availability are present, but the system has not made an active paid generation call for health purposes. `recent_operational_failure` means a related durable generation failure was recorded in the recent observation window.

This classification is intentionally conservative. Configuration alone never claims that a provider is healthy. The application does not make active paid generation calls merely to populate the dashboard.

For a provider incident, correlate the failed job with the provider category and safe HTTP/error fields. Rate-limit, timeout, configuration, malformed-response, and transient categories should be distinguished before changing a feature flag. Mubert retry logs include the bounded attempt number. Provider credentials must be rotated through Railway variables or the provider control plane, never through source files or log messages.

## Payment and webhook diagnosis

Customer charging is disabled by default and remains disabled for this release. A checkout request should return `CHECKOUT_DISABLED` while logging the disabled state. If a payment provider is configured in a non-production test environment, checkout and webhook events still pass through idempotent foundation state transitions.

For a webhook incident, search by `PaymentEventId` and inspect `PaymentEventStatus`. The expected progression is `Received` to `Processed`. A repeated provider event should produce `WEBHOOK_DUPLICATE` without applying the domain transition twice. A bad signature should produce `WEBHOOK_SIGNATURE_INVALID` and must not create a payment event. A verified event that cannot be applied is stored as `Rejected` with a safe failure reason and a structured error log.

For reconciliation failures, inspect `ReconciliationStatus` and the `ReconciliationId`. `Pending` and `Mismatch` records are counted in Admin Operations. Resolution is an internal lifecycle action and is not exposed as a destructive dashboard control.

Never log or copy raw webhook payloads, signatures, provider access tokens, payment secrets, or full provider object references into an incident channel.

## Railway troubleshooting workflow

Use the Railway project dashboard as the first control plane for deployment state, environment variables, service logs, and PostgreSQL status. The production topology contains Taslim Web, Taslim API, and managed PostgreSQL. The API remains deployed from its Dockerfile; the web service remains deployed from its web Dockerfile.

For an API incident, follow this sequence:

1. Open the API deployment and confirm the latest deployment is running rather than repeatedly restarting.
2. Request `/health/live` to separate process availability from dependency readiness.
3. Request `/health/ready` and record the status of `database` and `storage`.
4. Search structured logs by `X-Request-ID`, `JobId`, `PaymentEventId`, or `FileId`.
5. Compare the incident time with PostgreSQL and R2 provider status.
6. Confirm that optional provider flags and payment charging remain at their intended values.
7. If a migration failed, correct the deployment configuration and redeploy through the normal reviewed path. Do not reset production data.
8. After recovery, confirm a new readiness response, a successful safe test path, and stable worker logs. Record the exact deployment and migration identifiers in the incident notes.

For a web-only incident, check the web service deployment and same-origin `/api/*` proxy first. Do not alter Wave 4.1 Home/UI design as part of an observability incident.

## Validation checklist

Before pushing an observability change, run the complete API test project where the .NET SDK is available, run the frontend tests and build only when frontend files changed, run the API Release build and publish, and run `git diff --check`. Confirm that no source, configuration, test fixture, or generated log contains a secret or private document content. Confirm that charging remains disabled and providers remain unactivated.

## Known limitations

The API logs use the configured `ILogger` providers; centralized retention, alert thresholds, and log shipping are Railway deployment concerns rather than a second in-application monitoring architecture. Provider health does not make live provider calls and therefore cannot prove end-to-end provider availability. The recent-failure window is based on durable generation-job failures and does not replace provider status pages.

Lease recovery increments a durable retry count when a worker loses an expired claim. It does not automatically retry a user-cancelled job or a terminally failed job. A readiness storage probe verifies the configured adapter with a read-only check, but it cannot prove that every future object publication will succeed.

## References

[1]: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/logging/ "ASP.NET Core logging fundamentals"
[2]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks "ASP.NET Core health checks"
[3]: https://docs.railway.com/guides/healthchecks "Railway health checks"
[4]: https://developers.cloudflare.com/r2/api/s3/api/ "Cloudflare R2 S3-compatible API"
