# Taslim Social Studio Architecture

## Scope

Social Studio is a protected, provider-independent workflow at `/create/social`. It creates ready-to-review social copy for Instagram, Facebook, LinkedIn, X, TikTok, or a multi-platform brief. The feature does not publish directly to a social network, connect social credentials, schedule posts, or expose provider/model controls to normal users.

The workflow reuses the existing authenticated Next.js shell, CSRF-protected API, workspace authorization, durable Generation Job queue, private file storage, Asset Library, AI Core structured output, and Usage Ledger. No new database table or migration is required in Batch 3.13: the existing `GenerationJobs`, `GenerationJobOutputs`, `StoredFiles`, `Assets`, and `UsageTransactions` tables are sufficient. `UsageFeature.Social` is stored through the existing string-backed feature column.

## Request and context boundaries

The browser submits a bounded `SocialGenerationRequest`. It contains a brief, social type, platform, tone, language, optional audience/brand voice/call to action, output preferences, optional project ID, explicitly selected extracted source-file IDs, and explicitly selected existing Asset IDs. The form limits selections to five ready extracted files and eight active file-backed Assets.

The API validates workspace membership and project ownership before creating a job. Source files are limited to PDF, DOCX, TXT, Markdown, CSV, and XLSX and must be ready for extraction. Their extracted text is bounded by `SocialGeneration:MaxContextCharacters`. Selected Assets are also workspace-authorized and active; their names and safe metadata may guide visual or content references, while private storage keys and raw binary data remain server-only. No Personal Memory, unselected project files, or all-project-file collection is injected automatically.

Project instructions and context notes are included only when the user explicitly chooses a project and the project belongs to the selected workspace. The prompt builder clearly labels selected context, instructs the model not to invent factual claims, and requires asset references to use selected names rather than internal IDs.

## Canonical draft and provider boundary

AI Core receives a strict structured-output specification named `taslim_social_draft`. The canonical `SocialDraft` contains a title, platform, social type, language, and an ordered list of `SocialPost` records. Each post has a hook, body, optional call to action, hashtags, alt text, visual direction, and selected Asset references. It contains no arbitrary HTML, XML, executable markup, or provider metadata.

The provider adapter is `ISocialGenerationProvider`; the job handler never calls a vendor SDK or HTTP endpoint directly. The adapter uses the existing `IChatCompletionService` and AI Core structured-output path. It normalizes a narrowly supported JSON fence, deserializes the canonical draft, and validates post order, field lengths, language/platform/type values, hashtag shape, asset-reference bounds, and markup rejection. Malformed or non-canonical output fails safely and never becomes an Asset.

## Durable lifecycle

The controller accepts the request at `POST /api/social-generation/jobs` only for an authenticated user with a valid CSRF token. It runs request validation, the existing Usage Ledger preflight, and the existing job authorization checks. The job is persisted as `social.generate` and is processed by `SocialGenerationJobHandler` in the database-backed worker.

The handler reports bounded progress through validation, context preparation, provider generation, draft validation, and publication. Status is authoritative in the job snapshot. The web client polls with bounded retry backoff, caps active display progress at 99%, treats terminal status as authoritative, supports cooperative cancellation, and renders safe malformed-result states instead of assuming a successful payload shape.

On success, the canonical draft is serialized as a private JSON generated file. The worker publishes one `GenerationJobOutput` and one logical `social` Asset in the same atomic completion path used by the other studios. Failed, cancelled, storage-failed, or abandoned jobs discard prepared artifacts and do not create a partial Asset.

## Asset Library and review behavior

The generated Asset has type `social`, a safe title/description, and non-sensitive metadata such as platform, social type, language, post count, selected-source count, and selected-Asset count. Its file is a private `application/json` representation of the canonical social draft. Existing Asset Library operations continue to provide listing, filtering, rename, project assignment, archive, restore, and authorized download. The Studio completion view provides a readable post-by-post preview and a local copy action for hooks, bodies, CTAs, and hashtags.

The product intentionally stops at review. It does not auto-publish, schedule, or call a social-platform API. Future batches may add explicit user-authorized export or publishing adapters, but those must introduce separate credential ownership, consent, rate limits, audit events, and provider-specific security boundaries.

## Localization and RTL

Visible Social Studio strings exist in English, Arabic, and Kurdish Sorani. The existing locale provider controls `dir="ltr"` or `dir="rtl"`; the UI uses logical spacing and RTL-safe flex direction rules. Generated Arabic and Kurdish text is preserved as Unicode and is never reversed or normalized into Latin text.

## Usage accounting and privacy

The worker routes `social.generate` to `UsageFeature.Social`. Provider usage metadata, model key, latency, cost basis, pricing snapshot, and internal stage provenance remain in the existing Usage Ledger and administrator-only views. Customer charge remains zero. Normal-user job DTOs and Social result JSON contain only the safe Asset ID (when published), title, platform, language, post count, and canonical post preview. They do not expose provider names, model identifiers, prompts after enrichment, storage keys, credentials, pricing, raw upstream payloads, or exception details.

## Failure and security policy

Safe terminal codes distinguish invalid requests, unavailable selected context, oversized context, provider configuration/transient/rate-limit/unsupported failures, invalid output, storage/publication failures, and cooperative cancellation. User-facing messages are stable and actionable; durable logs contain only job ID, stage, safe code, exception type, safe provider classification, optional HTTP status/error code from AI Core, model key for internal diagnostics, structured-output flag, and elapsed time. Source text, social copy, credentials, cookies, and raw provider responses are excluded from diagnostics.

Workspace, project, source-file, Asset, generation-job, and private-download authorization remains server-side. The browser sends opaque IDs only. CSRF, Identity cookies, credentialed requests, storage adapters, Data Protection, and existing same-origin proxy behavior are unchanged.

## Future expansion

Future Social Studio work can add explicit image composition using selected Image Assets, reusable campaign collections, social-platform exports, approval workflow, and audited publishing integrations. Those are deliberately not part of this batch. The current model already supports selected Asset references without automatic Image Studio calls or implicit provider expansion.
