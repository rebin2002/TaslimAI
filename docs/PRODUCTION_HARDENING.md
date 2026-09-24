# TASLIM.AI Production Hardening — Wave 3.8

**Branch:** `parallel/wave3-production-hardening`  
**Production main reviewed:** `4fa0fd5`  
**Scope:** Reliability, security, and production hardening before broader public usage.  
**Assessment:** The branch addresses the highest-risk correctness and availability issues found in the audit. Customer charging, payment processing, and disabled AI providers remain disabled.

## Executive conclusion

The production baseline has sound security foundations. Authentication uses server-side cookie sessions, state-changing API routes use antiforgery validation, workspace resources are generally authorized through membership checks, private asset downloads resolve storage keys on the server, and non-development exceptions are returned through a generic error envelope. Payment and webhook implementations remain fail-closed foundations rather than active integrations.

The audit found four priority issues that could cause duplicate provider work, duplicate outputs, or service exhaustion. They are fixed in this branch. Generation jobs now use a creator-scoped idempotency key when supplied, and browser generation calls send one automatically for each user action. Running jobs renew their lease and fence every progress, terminal, cancellation, and failure update with the claimant token. S3-compatible downloads now stream the provider response rather than buffering the entire private object. The Next.js same-origin API proxy now rejects oversized bodies before forwarding and applies an upstream timeout with a safe error envelope.

The branch also validates actual byte counts for generated video streams and moves seekable buffering into the extraction path, where it is bounded and needed by document parsers. No customer charging or external provider was enabled as part of this work.

## Findings and disposition

### Critical

| Finding | Evidence | Disposition |
| --- | --- | --- |
| Expired generation leases could allow duplicate provider execution and stale publication. | `apps/api/Generation/GenerationJobExecution.cs` previously recovered a running job after a fixed 15-minute lease, while completion updates were filtered by job ID and status rather than the claimant token. | **Fixed.** Production lease duration is configurable and defaults to 30 minutes. A monitor renews the lease while work is active. Recovery assigns a new token. Progress, completion, cancellation, and failure updates require the active `ConcurrencyToken`; stale workers cannot publish or finalize a job after ownership is lost. |

### High

| Finding | Evidence | Disposition |
| --- | --- | --- |
| Generation creation was not idempotent across client retries. | `CreateGenerationJobRequest` had no request key. Each call generated a new job ID, so a timeout followed by a retry could invoke a provider twice. | **Fixed.** The API accepts `Idempotency-Key`, normalizes and bounds it to 80 characters, stores a request fingerprint, and enforces a unique `(CreatedByUserId, IdempotencyKey)` index. A matching retry returns the original job; a reused key with different input returns `IDEMPOTENCY_KEY_REUSED`. Web generation methods now attach a generated key that is preserved across the built-in CSRF retry. Legacy callers that do not send a key retain prior behavior and should migrate to the header. |
| S3/R2 downloads buffered the complete private object in memory before returning it. | `apps/api/Files/S3CompatibleFileStorageService.cs` copied `GetObjectResponse.ResponseStream` into a `MemoryStream`, while the controller enabled range processing afterward. | **Fixed.** The adapter returns a response-owned streaming wrapper. Disposing the HTTP response stream disposes the provider response as well. Extraction stages non-seekable streams separately and within the configured file limit; asset downloads remain streaming. |
| The Next.js API proxy buffered request bodies without a request limit or upstream deadline. | `apps/web/src/app/api/[...path]/route.ts` previously called `request.arrayBuffer()` and awaited `fetch()` without a size or timeout boundary. | **Fixed.** The proxy rejects declared or observed bodies over 25 MiB with `413 REQUEST_TOO_LARGE`, and aborts upstream calls after 30 seconds with `504 API_TIMEOUT`. Provider and network details are not returned to the browser. |

## Additional fixes

Generated video storage now wraps provider streams in a counting, hard-limited reader. The observed byte count must equal the declared size before a `StoredFile` becomes ready; partial output is marked failed and cleaned up. This prevents an underestimated size from bypassing the configured generated-video limit and prevents inaccurate metadata.

The S3 storage tests now verify that non-seekable responses are not fully buffered. The extraction tests verify that PDF extraction still succeeds because the extractor, rather than the download path, performs bounded seekable staging when a parser requires it.

Production worker settings now state a 30-minute lease and 60-second renewal interval in `apps/api/appsettings.Production.json`. The existing disabled payment provider, rejecting webhook verifier, disabled music and voice configuration, and zero customer charging behavior were preserved.

## Medium findings intentionally deferred

The following issues were identified but were not changed in this branch because they require broader operational or product decisions, additional infrastructure, or a larger test surface than the targeted safe fixes.

| Finding | Risk | Recommended follow-up |
| --- | --- | --- |
| Deactivated users can retain access through an already-issued persistent cookie until cookie expiry. | `IsActive` is checked during login and `/api/auth/me`, but not by a centralized cookie principal validator for every authorized request. | Add an ASP.NET Core cookie validation event or authorization requirement that reloads the user and rejects inactive principals. Cover project, file, asset, and job mutations after deactivation. |
| Office-file extraction does not yet enforce ZIP decompression and XML resource quotas. | A crafted DOCX/XLSX can create disproportionate CPU or memory work during synchronous extraction. | Add central-directory entry-count, uncompressed-byte, compression-ratio, XML-size, and streaming-parser limits. Keep the existing 25 MiB upload boundary. |
| `/health` is an unconditional 200 response and is not a readiness signal. | A process can be routed traffic while the database or a required dependency is unavailable. | Add separate dependency-independent liveness and bounded database readiness endpoints, then configure deployment checks to use readiness. |
| No application-level rate limiter or active generation cost guardrail protects expensive job creation. | An authenticated account can amplify queue load and provider usage. | Add identity, workspace, endpoint, and IP-aware limits with `429` and `Retry-After`; enforce queue/concurrency quotas. Enable cost guardrails only after an explicit production pricing and provider-readiness decision. |
| Application security headers are not configured in the Next app. | The deployment edge may not provide CSP, HSTS, clickjacking, MIME-sniffing, and referrer protections. | Add and verify a deliberate baseline such as CSP with `frame-ancestors 'none'`, HSTS on HTTPS, `nosniff`, and a restrictive referrer policy. |
| Production configuration can fall back to localhost defaults when required API/CORS settings are absent. | A misbuilt production image can fail in a misleading way or accept only a development origin. | Fail startup/build when production API URL and allowed origins are missing, non-HTTPS, or localhost. |
| Both application containers run as root. | A process compromise has a larger container-level impact. | Add dedicated unprivileged runtime users and enforce a non-root image policy in CI. |
| Some frontend components render arbitrary API error messages directly. | An overlooked endpoint could expose provider or diagnostic text to authenticated users. | Map user-facing errors from stable codes and allowlisted field errors; keep internal detail in bounded server logs. |
| Route-level recoverable error boundaries do not cover all high-value areas. | A render exception can leave account, chat, asset, or studio routes without a retry/navigation path. | Add root and high-value route boundaries with safe retry actions and correlation-only telemetry. |

## Authentication, authorization, storage, and payment audit notes

No current cross-workspace project, file, asset, representation, memory, usage, billing-read, or generation-job bypass was found in the reviewed routes. Workspace membership is checked on the server and storage keys are not returned as public URLs. Generated asset and representation downloads authorize the parent asset before opening the server-side storage key.

CSRF protection is applied at the API boundary and state-changing controller actions retain `[ValidateAntiForgeryToken]`. The hardening changes do not weaken that control. Session cookies remain `HttpOnly`, `Secure`, and `SameSite=None` for the cross-origin web/API deployment model.

Customer charging remains disabled through `Billing:CustomerChargingEnabled=false`. The registered payment provider remains `UnconfiguredPaymentProvider`, and the rejecting webhook verifier remains in place. No payment webhook route was exposed or enabled by this branch. When a real provider is introduced, raw-body signature verification, provider/entity binding, replay protection, idempotency, and reconciliation must be re-reviewed together.

## Database migration

The branch adds `20260924160000_AddGenerationJobIdempotency.cs` and updates `TaslimDbContextModelSnapshot.cs`.

The migration adds nullable `GenerationJobs.IdempotencyKey` and `GenerationJobs.RequestFingerprint` columns and creates a filtered unique index on `(CreatedByUserId, IdempotencyKey)`. The columns are nullable for compatibility with existing production jobs. New keyed requests always write a fingerprint.

The migration must be applied through the existing production migration startup path. It was not applied to a production database in this task.

## Validation

The intended validation sequence is:

```bash
npm run test --prefix apps/web
npm run lint --prefix apps/web
(cd apps/web && npx tsc --noEmit)

export PATH="/home/ubuntu/.dotnet:$PATH"
dotnet build apps/api/Taslim.Api.csproj
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj
```

Targeted regression coverage added or updated in this branch includes:

- same-key generation retries return one job and conflicting payloads are rejected;
- non-seekable S3/R2 responses remain streaming;
- PDF extraction continues to work through bounded extraction staging;
- oversized proxy bodies return `413` before an upstream request;
- aborted upstream requests return a safe `504` envelope;
- generated stream metadata is checked against observed bytes.

The verified results for this branch are as follows. The web suite passed all 82 tests. The web TypeScript check, ESLint check, and production `next build` passed. The API project built with zero errors and zero warnings. The focused idempotency and S3 streaming run passed 11/11 tests. The focused image-storage failure and music-unavailable suite passed 2/2, the isolated asset suite passed 3/3, the isolated social suite passed 2/2, and the proxy tests passed as part of the web suite.

The final two complete API runs passed 178/179, while an earlier run passed 176/179. Each non-green run failed in a different unrelated integration fixture with an HTTP 500 during test setup or a pending terminal record. Detailed logs showed the test worker querying an in-memory SQLite database before its fixture completed `EnsureCreated`; the isolated suites pass. This is recorded as a test-harness parallel-startup limitation, not as a claimed product pass. A follow-up should make test database initialization happen before worker startup or disable parallel fixture startup in the test project, then require one fully green run in CI.

## Remaining risks

The branch does not provide a full rate-limiting system, production readiness probing, active-user revocation, ZIP-bomb protection, security-header policy, non-root containers, or a real payment webhook implementation. Those items remain explicit launch risks rather than hidden assumptions. The application should not broaden public traffic or enable paid provider integrations until the medium items are assigned an operational owner and tested in a production-like environment.

The idempotency guarantee is strongest for callers that send `Idempotency-Key`. The browser client now does so for generation calls. Other integrations must adopt the header before relying on retry safety. Movie quick-project creation still creates project state before its internal job request; its idempotency key protects the job record but is an integration hotspot for a future end-to-end movie-operation key.

## Integration hotspots

1. **Generation callers:** all specialized generation controllers and movie generation endpoints now accept and forward `Idempotency-Key`. Any new generation endpoint must preserve this propagation and include the key in the operation boundary.
2. **Worker deployment:** multiple API instances can now safely recover leases only when all instances run this schema and code together. Apply the migration before rolling out mixed versions.
3. **Storage adapters:** `IFileStorageService.OpenReadAsync` now permits non-seekable streams. New extraction consumers must stage through the bounded extractor; new download consumers should preserve streaming and must not call `ToArray`, `MemoryStream`, or equivalent full-object buffering.
4. **Reverse proxy and edge:** the Next proxy limit is 25 MiB and the upstream timeout is 30 seconds. Edge limits and provider-specific timeouts should be equal to or stricter than these values, not looser without a capacity review.
5. **Provider enablement:** payment, music, voice, and unfinished external integrations remain disabled. Enabling any of them requires a separate provider credential, webhook, retry, cost, and incident-response review.

## References

[1]: `./ARCHITECTURE.md` "Taslim.ai architecture and ownership model"
[2]: `./AUTHENTICATION.md` "Taslim.ai authentication and CSRF design"
[3]: `./FILES_ARCHITECTURE.md` "Taslim.ai private file and storage architecture"
[4]: `./GENERATION_JOBS_ARCHITECTURE.md` "Taslim.ai generation job lifecycle and cancellation design"
[5]: `./PAYMENTS_AND_CHECKOUT_ARCHITECTURE.md` "Taslim.ai payment and checkout foundation"
