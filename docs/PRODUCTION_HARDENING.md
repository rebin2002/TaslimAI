# TASLIM.AI Production Hardening — Phase 2

**Branch:** `parallel/hardening-phase-2`
**Starting `origin/main` SHA:** `c9489fdb3efab0ca9e66115805c02ce4b2f184b5`
**Scope:** Safe, high-value Medium/Low production-hardening controls remaining after Wave 3.8.
**Assessment:** This branch adds application abuse controls, security headers, bounded archive extraction, strict production configuration checks, a liveness/readiness distinction, non-root container runtimes, centralized disabled-user enforcement, and additional safe failure handling. Customer charging and all optional providers remain disabled or unconfigured.

## Executive conclusion

The Phase 2 changes address the findings that could be implemented without changing product behavior or destabilizing authentication, billing, or provider selection. Rate limits are applied to abuse-sensitive operations, including authentication, chat generation, generation-job creation, uploads, search, and expensive AI operations. Rejected requests return a stable `429` envelope with `Retry-After: 60` and no infrastructure details.

The Next.js application and API now emit a deliberate baseline of security headers. The policy permits the existing Next.js runtime, same-origin proxy, local development API fallback, required image/font sources, and streaming connections without introducing a production-only CSP that blocks the current application. Production web builds require an explicit HTTPS API origin instead of silently embedding localhost.

DOCX and XLSX extraction now validates archive entry counts, total uncompressed bytes, per-entry bytes, compression ratios, path safety, and XML parser quotas before parsing. Extraction remains bounded by the existing upload and extracted-text limits, and archive failures return the existing safe `EXTRACTION_FAILED` result.

The API retains `/health` as a dependency-independent liveness endpoint and adds `/readiness`, which performs a bounded database connectivity check and returns only `ready` or `not_ready`. Production configuration fails startup when critical database, CORS, persistent-storage, or explicitly enabled provider settings are invalid. Disabled providers do not require credentials. Authenticated cookies are revalidated against `ApplicationUser.IsActive` on every request so deactivated users are rejected centrally rather than retaining access until cookie expiry.

## Findings and disposition

| Finding | Disposition | Evidence and rationale |
| --- | --- | --- |
| No application-level rate limiter protected authentication, chat, generation, upload, search, or expensive AI operations. | **Fixed.** | `RateLimiting` defines identity/IP-partitioned policies. Authentication uses 60 requests/minute per route and client; chat uses a 30-token/minute bucket; generation creation uses 12 requests/minute; uploads use 20 requests/minute; search uses 120 requests/minute; expensive AI creation uses 10 requests/minute. All queues are non-blocking and return a safe `429` response. These are request-abuse limits, not charging or provider-cost semantics. |
| Application security headers were not configured in the Next app. | **Fixed.** | `apps/web/next.config.ts` adds `nosniff`, strict-origin referrer handling, `DENY` frame protection, `frame-ancestors 'none'`, a compatible CSP, and a restrictive Permissions Policy. Production responses also add HSTS. The API applies the same baseline to API responses and adds HSTS only when the request is HTTPS. |
| Office-file extraction did not enforce ZIP decompression, entry-count, path, or XML quotas. | **Fixed.** | `FileContentExtractor` bounds archive entries to 256, total uncompressed bytes to 64 MiB, each entry to 16 MiB, compression ratio to 100:1, and XML document characters to 8 MiB. Absolute, traversal, empty, NUL-containing, and dot-segment paths are rejected. The archive is disposed on validation failure. |
| Production configuration could fall back to localhost or placeholders. | **Fixed.** | Production startup requires a non-placeholder database setting, at least one HTTPS non-loopback `AllowedOrigins` value, complete HTTPS S3-compatible storage settings when that provider is selected, and credentials only when OpenAI is explicitly enabled. Customer charging is rejected if enabled. Local development and the testing environment retain their existing defaults. |
| `/health` was unconditional and did not represent readiness. | **Fixed.** | `/health` remains a safe liveness response. `/readiness` checks database connectivity and returns `200 {"status":"ready"}` or `503 {"status":"not_ready"}` without connection strings, credentials, provider details, or exception text. Deployment should route readiness checks to `/readiness` and use `/health` for process liveness. |
| Application containers ran as root. | **Fixed.** | Both API and web Dockerfiles create dedicated unprivileged runtime users. The API retains port 8080, required font packages, and Railway-compatible entrypoint behavior. The web image retains port 3000 and Next.js startup behavior. |
| Deactivated users could retain access through an already-issued persistent cookie. | **Fixed.** | ASP.NET Core Identity cookie validation reloads the user by ID for each authentication event, rejects missing or inactive users, and signs out the application cookie. No controller-by-controller duplication was added. |
| Some backend failures needed broader sanitization. | **Partially fixed / reviewed.** | Rate-limit, proxy timeout, proxy unavailable, readiness, and unhandled production exception paths use stable envelopes without infrastructure details. Existing controller exception messages are limited to purpose-built validation exception types and existing safe user-facing messages. Provider failures continue to be logged server-side and mapped to stable failure codes. |

## Rate limits implemented

| Policy | Partition | Limit | Applied to |
| --- | --- | --- | --- |
| `authentication` | Anonymous client IP, or authenticated user, plus route | 60/minute | Registration and login |
| `chat-generation` | Authenticated user, or client IP | 30 tokens/minute | Chat send, streaming send, and regeneration |
| `generation-creation` | Authenticated user, or client IP | 12/minute | Generic generation-job creation |
| `uploads` | Authenticated user, or client IP | 20/minute | Multipart file upload |
| `search` | Authenticated user, or client IP | 120/minute | Global search |
| `expensive-ai` | Authenticated user, or client IP | 10/minute | Image, document, presentation, research, social, music, voice, movie scene, and movie shot generation |

The policies are intentionally scoped to creation or expensive operations. Read-only job polling, ordinary asset/file reads, conversation listing, and movie-studio editing are not placed behind the expensive-operation policies. A rate-limit response is `429`, includes `Retry-After: 60`, and returns `RATE_LIMITED` with a generic message.

These limits do not activate charging, enable a provider, or change usage-ledger or pricing behavior. Queue/concurrency and cost guardrails remain separate product and capacity decisions.

## Security headers

The web service emits the following baseline for all routes:

- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `X-Frame-Options: DENY`
- `Content-Security-Policy` with `frame-ancestors 'none'`, `object-src 'none'`, same-origin form actions, and compatible Next.js script/style, image, font, and connection sources
- `Permissions-Policy` disabling camera, geolocation, microphone, payment, and USB features
- `Strict-Transport-Security: max-age=31536000; includeSubDomains` in production

The API emits the same baseline for JSON and health/readiness responses. The API CSP is intentionally non-document-oriented (`default-src 'none'`) because API responses are not application documents. The deployment edge remains responsible for any stricter organization-wide headers.

## ZIP and extraction limits

The existing 25 MiB upload limit remains in effect. Archive-specific limits are configured under `Files`:

| Setting | Production value |
| --- | ---: |
| `MaxArchiveEntries` | 256 |
| `MaxArchiveUncompressedBytes` | 67,108,864 bytes |
| `MaxArchiveEntryBytes` | 16,777,216 bytes |
| `MaxArchiveCompressionRatio` | 100:1 |
| `MaxArchiveXmlCharacters` | 8,388,608 characters |
| `MaxExtractedTextCharacters` | 80,000 characters |

DOCX and XLSX parsing uses an XML reader with DTD processing prohibited, no resolver, and a maximum document-character quota. No archive is extracted to an uncontrolled filesystem location. Unsafe paths are rejected even though the current parser reads selected entries directly.

## Production configuration requirements

Production deployments must provide the following without placing secrets in source control:

| Configuration | Requirement |
| --- | --- |
| `ConnectionStrings__Postgres`, `ConnectionStrings:Postgres`, or `DATABASE_URL` | Required and not a placeholder. PostgreSQL URI values continue to be normalized by the existing resolver. |
| `AllowedOrigins__0`, `AllowedOrigins__1`, ... | At least one trusted HTTPS origin. Localhost, loopback, user-info-bearing, and non-HTTPS origins are rejected. |
| `Files:StorageProvider` | `S3Compatible` in production. |
| `Files:S3Endpoint` | HTTPS endpoint. |
| `Files:S3Region`, `Files:S3Bucket`, `Files:S3AccessKey`, `Files:S3SecretKey` | Required only because production selects S3-compatible storage. |
| `Ai:OpenAI:Enabled` | Remains `false` unless a separate provider-readiness decision explicitly enables it. If set to `true`, `Ai:OpenAI:ApiKey` and an HTTPS `Ai:OpenAI:BaseUrl` become required. |
| `Billing:CustomerChargingEnabled` | Must remain `false`. |
| `NEXT_PUBLIC_API_URL` | Required for the production web image and must be an explicit HTTPS URL. The Docker build no longer defaults this value to localhost. |

Disabled music and voice providers remain disabled and do not require credentials. No provider was enabled, no credential was added, and no pricing or billing semantic was changed.

## Readiness and health

`GET /health` is dependency-independent and suitable for liveness. `GET /readiness` performs a bounded database `CanConnectAsync` check. A failed dependency returns a generic `503` response and logs the exception only on the server. Neither endpoint returns secrets, connection strings, storage credentials, provider credentials, or exception details.

## Container/runtime changes

The API runtime image creates and uses the `app` system user after copying the published application. The web runtime image creates and uses an `app` user after copying the Next.js runtime output. Build stages retain the existing SDK/base images and package requirements. Railway port declarations, startup commands, migration startup behavior, and required API font packages are preserved.

## Deferred items

The following findings remain explicitly deferred:

| Finding | Reason deferred |
| --- | --- |
| Queue/concurrency quotas and active cost guardrails for generation work | Requires a production capacity, provider-availability, and pricing decision. `UsageControls:GuardrailsEnabled` remains `false`. The new request rate limits provide abuse resistance without changing billing semantics. |
| Full frontend stable-code mapping for every backend error and complete route-level error boundaries | These require broader frontend behavior and copy/UI work. Only backend-safe responses directly required by this phase were changed; the Wave 4.1 Home/UI redesign was not touched. |
| Organization-wide edge/WAF rate limits, HSTS policy ownership, and deployment health-check wiring | The application controls are in place, but Railway/edge configuration is operational and must be applied by the deployment owner. `/readiness` is the intended readiness path. |
| Payment webhook/provider implementation and customer charging | Explicitly out of scope. The unconfigured payment provider, rejecting webhook foundation, zero charging behavior, and pricing remain unchanged. |
| Enabling OpenAI, Voice, Music, Movie, or other external providers | Explicitly out of scope. Provider credentials were not added and no disabled provider was activated. |
| Full test-fixture startup race cleanup | The suite is green in this run. The test factory still relies on the existing in-memory database startup pattern; future changes should preserve the explicit fixture initialization before worker activity. |

## Database migrations

No migration was added. These controls use existing user, configuration, storage, and health infrastructure. The Phase 2 branch does not redo the Wave 3.8 generation lease, claimant fencing, generation idempotency, usage finalization, streaming, request-size, upstream-timeout, or generated-output-limit work.

## Validation

The following checks passed on this branch:

```text
API Release build: 0 errors, 0 warnings
API complete test suite: 196 passed, 0 failed, 0 skipped
API focused Phase 2 tests: 3 passed, 0 failed
API forwarded-header production tests: 2 passed, 0 failed
Web complete test suite: 99 passed across 26 files
Web ESLint: passed
Web TypeScript check: passed
Web production build with NEXT_PUBLIC_API_URL=https://api.example.test: passed
Git diff --check: passed
```

The complete API suite was rerun after the final test expectation correction and completed `196/196` green. The focused hardening tests cover safe health/readiness output, security headers, disabled-user rejection, archive path traversal rejection, and archive expansion rejection. Existing forwarded-header tests continue to cover production secure-cookie behavior.

## Security and deployment confirmations

- **Charging remains disabled:** `Billing:CustomerChargingEnabled=false` is preserved and production startup rejects an accidental `true` value.
- **Providers remain disabled or unconfigured:** music and voice remain disabled; payment remains unconfigured; OpenAI credentials are not required while OpenAI is disabled.
- **No real secrets were added:** production credentials remain environment/configuration responsibilities. Test-only values are non-secret fixture values in an existing production test host.
- **Wave 4.1 Home/UI redesign was not modified:** no Home page, global stylesheet, shell, or redesign component was changed.
- **Main remains unchanged:** this work is on `parallel/hardening-phase-2` and is not merged to `main`.

## References

[1]: `./ARCHITECTURE.md` "Taslim.ai architecture and deployment model"
[2]: `./AUTHENTICATION.md` "Taslim.ai authentication and CSRF design"
[3]: `./FILES_ARCHITECTURE.md` "Taslim.ai private file and storage architecture"
[4]: `./GENERATION_JOBS_ARCHITECTURE.md` "Taslim.ai generation job lifecycle and cancellation design"
[5]: `./PAYMENTS_AND_CHECKOUT_ARCHITECTURE.md` "Taslim.ai payment and checkout foundation"
