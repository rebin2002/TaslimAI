# Taslim.ai

Taslim.ai is a multilingual AI platform foundation designed to make professional AI capabilities simple, fast, and approachable. **Batch 3.2 adds the first production AI provider and provider-independent streaming. Batch 3.3 adds the internal usage ledger and zero-charge accounting foundation. Batch 3.4 adds user-approved memory and project-scoped context. Batch 3.5 adds secure file storage, bounded document extraction, and explicit chat attachments. Batch 3.6 adds the persistent provider-independent Generation Job foundation. Batch 3.7 adds the unified reusable Asset architecture and product library. Batch 3.8 adds the first real Image Studio on top of those foundations. Batch 3.9 adds versioned provider-cost accounting, opt-in safety controls, anomaly flags, and a server-authorized admin usage dashboard without activating customer billing. Batch 3.10 adds the first real Document Studio workflow with canonical drafting and DOCX/PDF output. Batch 3.11 adds provider-independent Presentation Studio with strict canonical drafting, managed editable PPTX output, and RTL support. Batch 3.12 adds cited Research Studio with server-side hosted web search and reviewable DOCX/PDF reports. Batch 3.13 adds a provider-independent Social Studio for bounded, reviewable social content drafts.**

## Architecture

```text
Taslim Web (Next.js)
        |
        | credentialed HTTPS + CSRF header
        v
Taslim API (ASP.NET Core Identity + Web API)
        |
        +---- PostgreSQL (EF Core / Npgsql)
```

The browser owns presentation and navigation. The API owns authentication, authorization, persistence, and AI Core orchestration. Provider secrets must never be sent to browser clients. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/AUTHENTICATION.md](docs/AUTHENTICATION.md), [docs/CHAT_ARCHITECTURE.md](docs/CHAT_ARCHITECTURE.md), [docs/FILES_ARCHITECTURE.md](docs/FILES_ARCHITECTURE.md), [docs/GENERATION_JOBS_ARCHITECTURE.md](docs/GENERATION_JOBS_ARCHITECTURE.md), [docs/ASSET_ARCHITECTURE.md](docs/ASSET_ARCHITECTURE.md), [docs/IMAGE_STUDIO_ARCHITECTURE.md](docs/IMAGE_STUDIO_ARCHITECTURE.md), [docs/DOCUMENT_STUDIO_ARCHITECTURE.md](docs/DOCUMENT_STUDIO_ARCHITECTURE.md), [docs/PRESENTATION_STUDIO_ARCHITECTURE.md](docs/PRESENTATION_STUDIO_ARCHITECTURE.md), [docs/RESEARCH_STUDIO_ARCHITECTURE.md](docs/RESEARCH_STUDIO_ARCHITECTURE.md), [docs/SOCIAL_STUDIO_ARCHITECTURE.md](docs/SOCIAL_STUDIO_ARCHITECTURE.md), and [docs/USAGE_ACCOUNTING_ARCHITECTURE.md](docs/USAGE_ACCOUNTING_ARCHITECTURE.md).

## Repository structure

```text
apps/
  web/                  Next.js App Router frontend
  api/                  ASP.NET Core Web API
  api.Tests/            Identity and ownership integration tests
packages/
  contracts/            Reserved for shared schemas/contracts
docs/
  ARCHITECTURE.md       System boundaries and extension path
  AUTHENTICATION.md     Cookie, CSRF, ownership, and migration details
  CHAT_ARCHITECTURE.md  Chat persistence, AI Core, provider path, and security
  FILES_ARCHITECTURE.md File storage, extraction, attachments, and security
  GENERATION_JOBS_ARCHITECTURE.md Persistent job lifecycle, queue, worker, and handlers
  ASSET_ARCHITECTURE.md Unified Asset model, publication, private downloads, lifecycle, and UI
  IMAGE_STUDIO_ARCHITECTURE.md Image request, provider, output, usage, safety, and UI boundaries
  DOCUMENT_STUDIO_ARCHITECTURE.md Canonical document drafting, rendering, Asset publication, and security
  PRESENTATION_STUDIO_ARCHITECTURE.md Canonical presentation drafting, managed PPTX rendering, RTL, and security
  RESEARCH_STUDIO_ARCHITECTURE.md     Cited web research, evidence normalization, reports, and security
  SOCIAL_STUDIO_ARCHITECTURE.md       Social drafts, selected context, Asset publication, and security
  USAGE_ACCOUNTING_ARCHITECTURE.md Versioned cost ledger, guardrails, anomalies, admin reports, and privacy
```

## Requirements

- Node.js 22+
- npm 10+
- .NET SDK 8.0+
- PostgreSQL 15+ for local database work

## Local development

```bash
git clone https://github.com/rebin2002/TaslimAI.git
cd TaslimAI
cd apps/web
npm install
```

Copy environment examples and adjust values. Never commit `.env` files:

```bash
cp apps/web/.env.example apps/web/.env.local
cp apps/api/.env.example apps/api/.env
```

### Frontend

```bash
npm run dev --workspace @taslim/web
```

The web app runs at `http://localhost:3000`. Set `NEXT_PUBLIC_API_URL=http://localhost:5000` in `apps/web/.env.local`.

Checks:

```bash
npm run lint --workspace @taslim/web
npm run build --workspace @taslim/web
```

### Backend

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/api --urls http://localhost:5000
```

The API runs at `http://localhost:5000`. Health check:

```bash
curl http://localhost:5000/health
# {"status":"healthy","service":"Taslim API"}
```

Swagger is available in Development at `/swagger`.

### PostgreSQL configuration

The API reads `ConnectionStrings__Postgres` or `DATABASE_URL`. It accepts either a standard Npgsql connection string or Railway’s URI format, such as `postgresql://user:password@host:port/database`; URI values are normalized server-side and are never logged.

For local work:

```text
ConnectionStrings__Postgres=Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me
Database__ApplyMigrations=false
```

Batch 2 uses EF Core migrations. It does not call `EnsureCreated()` or reset the database.

## Authentication and security

ASP.NET Core Identity issues an HttpOnly `taslim.auth` cookie. In Production it is Secure and configured for credentialed Web/API requests across the separate Railway origins. The frontend always sends `credentials: "include"` and uses `GET /api/auth/csrf` to obtain a request token for state-changing requests, which is sent through `X-CSRF-TOKEN`.

The API allows only configured origins and uses `AllowCredentials()`; wildcard CORS is not used. Private resources are authorized through Workspace membership. Projects are never directly owned by the browser user, and no authentication token is stored in localStorage.

### Railway HTTPS forwarding

Railway terminates TLS at its ingress proxy, so the API uses ASP.NET Core Forwarded Headers Middleware to consume one `X-Forwarded-Proto` hop before exception handling, CORS, authentication, authorization, or antiforgery. Production processes only the forwarded scheme, with `ForwardLimit=1`, and preserves `CookieSecurePolicy.Always` for both auth and CSRF cookies. Local development remains direct HTTP with the existing `SameAsRequest` development policy.

## Product flows through Batch 3.7

- Register at `/register`
- Sign in at `/login`
- View and update profile at `/account`
- Automatically receive a Personal Workspace
- Create and edit projects at `/projects`
- Open a project at `/projects/[projectId]`
- Edit project instructions and context notes at `/projects/[projectId]`
- Archive and restore projects without physical deletion
- View active and archived project lists
- Open a new chat at `/chat`
- Open an authorized conversation at `/chat/{conversationId}`
- Search, rename, archive, and revisit conversations
- Send messages through the provider-independent AI Core
- Stream assistant responses through Taslim-owned SSE events
- Retry failed generations without duplicating the user message
- Use OpenAI in Production when explicitly enabled on the API service
- Keep the mock provider for local development and tests
- Manage user-approved reusable memory at `/account/memory`
- Upload and manage project files at `/projects/[projectId]`
- Attach explicitly selected ready files to chat messages
- Validate the persistent system test job foundation at `/account/generation-jobs`
- Create an image asynchronously at `/create/image`, poll progress, cancel eligible jobs, and preview/download the private result
- Create a document asynchronously at `/create/document`, select source files and guided settings, poll progress, preview the canonical draft, and download private DOCX/PDF representations
- Create an editable presentation asynchronously at `/create/presentation`, select guided settings and explicit source files, poll progress, preview slides, and download the private PPTX representation
- Create a cited research report asynchronously at `/create/research`, choose web/source inputs and bounded research settings, review evidence and citations, and download private DOCX/PDF representations
- Create bounded social content asynchronously at `/create/social`, choose a platform and tone, optionally select project context, extracted sources, and existing Assets, review posts, and copy the result without direct publishing
- Find, filter, rename, reassign, archive, restore, and download reusable outputs at `/assets`
- View project-assigned Assets from `/projects/[projectId]`
- Review internal provider-cost summaries and transaction details at `/account/admin/usage` when the authenticated account has the `TaslimAdministrator` role

Supported project types are General, Movie, Marketing, Business, Research, Education, and Development. These are extensible server-side values, not a closed database enum.

## Migrations

Restore the pinned EF tool:

```bash
dotnet tool restore
```

Create a migration from the repository root:

```bash
dotnet tool run dotnet-ef migrations add <MigrationName> \
  --project apps/api --startup-project apps/api \
  --output-dir Persistence/Migrations
```

The initial migration is `InitialIdentityWorkspacesProjects` and creates ASP.NET Identity tables plus `Workspaces`, `WorkspaceMembers`, and `Projects`. Batch 3.1 adds `AddChatConversationsAndMessages` for `Conversations` and `ChatMessages`. Batch 3.2 adds `AddChatUsageAndIdempotency` for cached-token usage and duplicate-request protection, followed by `AddDeterministicChatMessageOrdering` for monotonic per-conversation message sequences and legacy backfill. Batch 3.3 adds `AddUsageLedger` for usage transactions, cost metadata, and request/feature idempotency. Batch 3.4 adds `AddPersonalMemoryAndProjectContext` for `PersonalMemories` and nullable project context fields. Batch 3.5 adds `AddStoredFilesAndChatAttachments` for `StoredFiles` and normalized `ChatMessageAttachments`. Batch 3.6 adds `AddGenerationJobs` for durable `GenerationJobs` and `GenerationJobOutputs`. Batch 3.7 adds `AddAssets` for the reusable user-facing Asset library and its storage/provenance relationships. Batch 3.9 adds `AddUsageAccountingFoundation` for versioned price snapshots, image quantities, cancellation/refund/anomaly fields, report indexes, and optional Generation Job provenance, followed by `AddUsageCostBasis` for the nullable actual-versus-estimated field. Batch 3.10 adds `AddAssetRepresentations` for multiple secure DOCX/PDF formats under one logical Asset. Batch 3.12 adds `AddResearchSourcesAndEvidence` for job-scoped source and evidence provenance. All additive migrations leave the existing `DataProtectionKeys` table and mapping intact.

To apply migrations locally against an explicitly selected database:

```bash
ASPNETCORE_ENVIRONMENT=Production \
Database__ApplyMigrations=true \
ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me' \
dotnet run --project apps/api
```

Production startup migrations use a PostgreSQL advisory lock and fail clearly if a migration cannot be applied. Batch 3.8 adds no schema migration, Batch 3.9 uses the additive accounting migration above, Batch 3.10 uses the additive AssetRepresentation migration, Batch 3.11 adds no migration because PPTX is an accepted generated-file/representation path using the existing schema, Batch 3.12 adds the source/evidence migration above, and Batch 3.13 adds no migration because Social Studio uses the existing job, generated-file, Asset, and Usage Ledger tables. All reuse the existing job, asset, storage, Identity, and Data Protection architecture. Do not run development reset commands against Production.

## Tests

Run the API integration suite:

```bash
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj
```

The suite covers registration, duplicate email, login failure, session/logout, personal workspace ownership, project lifecycle, chat persistence, mock AI execution, tier routing, provider failure, context trimming, usage/cost calculation, provider-independent stream events, SSE persistence, terminal failure events, deterministic multi-turn ordering, idempotency, usage ledger behavior, explicit cancellation/refund state, versioned pricing snapshots, actual-versus-estimated image accounting, provider latency and standard image usage parsing, disabled and enabled cost guardrails, admin role authorization, admin summaries/breakdowns/transactions/inspection, normal-user provider/model/pricing redaction, message history, cross-workspace authorization, CSRF enforcement, anonymous login CSRF recovery, credentialed CORS, no-cache token issuance, upload validation, bounded extraction, file ownership, normalized chat attachments, durable Generation Job claiming/cancellation/recovery, generated-file publication, Asset provenance, project inheritance, search/filter/pagination, archive/restore, authorized download, failed/cancelled no-Asset behavior, Image Studio validation, provider-independent deterministic image execution, private PNG publication, image usage cost, and safe provider refusal. Social Studio coverage includes strict canonical schema/HTML rejection, selected-context bounds, authorization, durable completion, JSON Asset publication, safe result preview, and zero customer charge. Frontend Vitest coverage includes streaming lifecycle, authentication error classification, one-shot CSRF refresh/retry, same-origin proxy forwarding, Asset Library loading, empty, update, archive, restore, safe-display, and card rendering states, Image Studio progress/result/cancellation and provider-detail redaction states, Social Studio progress/result/malformed-output/copy/source-readiness states, and admin usage date/chart state. Tests use relational SQLite so transactions and foreign keys are exercised realistically.

## Railway deployment

Production infrastructure uses three services: **Taslim Web**, **Taslim API**, and managed **PostgreSQL**.

### Taslim Web

- Root directory: `/apps/web`
- Builder: Dockerfile (recommended and required when using the included multi-stage Docker build)
- Dockerfile path: `Dockerfile`
- Custom build command: empty; the Dockerfile runs `npm ci` and `npm run build`
- Custom start command: empty; the Dockerfile runs `npm run start`
- Required variable: `NEXT_PUBLIC_API_URL=https://taslim-api-production.up.railway.app`
- Port: Railway-provided `PORT`

`NEXT_PUBLIC_API_URL` is a public Next.js variable and is embedded during `next build`. The web Dockerfile explicitly declares it as a Docker `ARG` in the builder stage and promotes it to `ENV` before `npm run build`; Railway injects service variables into Docker builds only when they are declared with `ARG`. Do not add the API URL to application source code. In Production, browser calls use the same-origin Next.js `/api/*` proxy, which forwards cookies and CSRF headers to the API; this avoids mobile-browser cross-origin cookie acceptance problems. Local development keeps direct calls to `http://localhost:5000`, matching `apps/web/.env.example`.

In Railway, keep `NEXT_PUBLIC_API_URL` configured on the Taslim Web service and use the Dockerfile builder. No custom build command is required; the Dockerfile performs the build. The API's exact `AllowedOrigins__0` setting remains required for direct API access, API tests, and future custom-domain use; the production browser proxy itself is same-origin and does not require wildcard CORS.

### Taslim API

**Preserve Dockerfile deployment. Do not switch the API back to Railpack.**

- Root directory: `/apps/api`
- Builder: Dockerfile
- Dockerfile path: `Dockerfile`
- Custom build command: empty
- Custom start command: empty
- Required variables:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Postgres=${{Postgres.DATABASE_URL}}
AllowedOrigins__0=https://taslim-web-production.up.railway.app
Ai__OpenAI__Enabled=true
Ai__OpenAI__ApiKey=<SECRET>
Ai__DefaultChatTier=Smart

# Optional: already-registered administrator email (role bootstrap only)
Admin__BootstrapEmails__0=<ADMIN_EMAIL>

# File context (API only)
Files__StorageProvider=S3Compatible
Files__S3Endpoint=https://<ACCOUNT_ID>.r2.cloudflarestorage.com
Files__S3Region=auto
Files__S3Bucket=<R2_BUCKET_NAME>
Files__S3AccessKey=<R2_ACCESS_KEY_ID>
Files__S3SecretKey=<R2_SECRET_ACCESS_KEY>
Files__MaxFileSizeBytes=26214400
Files__MaxAttachmentsPerMessage=5
Files__FileContextBudgetTokens=4000
Files__MaxExtractedTextCharacters=80000
Ai__FileContextBudgetTokens=4000
```

`appsettings.Production.json` enables `Database__ApplyMigrations=true`. The API Dockerfile uses a multi-stage .NET 8 build and binds to port 8080. Railway should route its provided service port to the container; if the platform requires an explicit variable, set `ASPNETCORE_HTTP_PORTS=8080`.

The OpenAI key belongs only on the Taslim API service. Do not add it to Taslim Web variables, source code, Docker build arguments, or browser bundles. `Ai__OpenAI__BaseUrl` is optional and defaults server-side to `https://api.openai.com/v1`. `Ai__DefaultChatTier=Smart` is the internal default; ordinary users do not select provider models. Production disables mock fallback, so a missing/failed OpenAI configuration returns a safe generation error rather than a simulated answer. Image Studio reuses the same existing API key through the server-only Images API adapter; its enabled flag, model, limits, moderation, and configurable token rates are committed API settings under `ImageGeneration` and can be overridden server-side if needed, but no new Railway variable is required. The existing `Ai__OpenAI__Enabled=true` and `Ai__OpenAI__ApiKey` settings must remain configured for both chat and image generation. For R2, the API uses the AWS SDK S3 adapter with the private account endpoint, region `auto`, bucket, access key, and secret shown above; none of these variables belong on Taslim Web. If R2 settings are incomplete or the provider name is unknown, the API returns a safe storage-unavailable error and never falls back to ephemeral Railway filesystem storage. No new Railway variable is required for forwarded HTTPS. If a future hosting topology uses a fixed private proxy, explicit proxy IPs may be supplied as `ForwardedHeaders:KnownProxies:0`, `ForwardedHeaders:KnownProxies:1`, and so on; do not add arbitrary client IPs.

### PostgreSQL

Use Railway’s managed PostgreSQL service and a private service reference for the API connection string. Do not commit credentials or replace the Railway reference with a hard-coded value. No manual migration command is required after deployment when Production startup migrations are enabled; monitor the first API deployment logs for the migration completion or a clear startup failure.

## Scope boundary

Included through Batch 3.13: the conversation and streaming foundation, server-only OpenAI chat adapter, internal model routing, zero-charge usage ledger with versioned provider-cost accounting, opt-in usage safety controls, server-authorized admin reporting, user-approved memory, project context, secure private file storage, bounded extraction, explicit chat attachments, durable provider-independent Generation Jobs, generated-output publication, the unified reusable Asset Library, the focused Image Studio MVP, Document Studio, Presentation Studio, cited Research Studio, and reviewable Social Studio drafts. Authentication, CSRF, workspace/project authorization, localization, RTL behavior, persistent Data Protection, and Railway deployment architecture remain intact.

Not included: Anthropic, Gemini, automatic cross-provider fallback, Movie, Voice, Music, direct social-platform publishing or scheduling, image editing/reference-image workflows, document editing/templates/versioning, vector databases, embeddings, RAG, tool calling, agents, customer billing, credits, subscriptions, payment processing, team chat sharing, invitations, business workspace creation, social login, or native mobile apps. Batch 3.8 adds one server-only OpenAI image generation path, Batch 3.9 adds operational accounting administration only, Batch 3.10 adds one server-side document drafting/rendering path, Batch 3.11 adds managed editable PPTX Presentation Studio, Batch 3.12 adds cited Research Studio with server-side hosted web search, and Batch 3.13 adds reviewable Social Studio drafts without direct publishing; none exposes provider choice to users or activates customer billing.

## Batch 3.3 usage ledger

Batch 3.3 adds the extensible `UsageTransaction` ledger and the `AddUsageLedger` migration. Chat creates one workspace-scoped pending transaction per `RequestId` and `Chat` feature, then marks it Completed only after provider success and usage metadata are known. Failed generations remain Failed with zero customer charge. Provider cost is calculated with the existing AI model catalog; no prices are duplicated in the ledger layer, and no real customer billing or payment gateway is active.

Authenticated workspace usage endpoints are available at `GET /api/workspaces/{workspaceId}/usage/summary` and `GET /api/workspaces/{workspaceId}/usage?page=1&pageSize=20`. The Account page links to `/account/usage`, which shows localized English, Arabic, and Kurdish Sorani totals and paginated history. Normal usage responses intentionally omit provider costs, provider names, model names, pricing snapshots, and anomaly metadata. Admin-only `/api/admin/usage/*` endpoints and `/account/admin/usage` expose those internal fields only after server-side `TaslimAdministrator` role authorization. Configure optional role bootstrap emails under `Admin:BootstrapEmails` during a migration-enabled startup; no default administrator is created.

## Batch 3.4 memory and project context

Batch 3.4 adds explicit user-approved context without automatic memory extraction. Authenticated users can manage active manual memories through `GET/POST /api/workspaces/{workspaceId}/memories`, `PATCH /api/memories/{memoryId}`, and `DELETE /api/memories/{memoryId}`. Memory records are scoped to the creating user and workspace, use bounded title/content fields and an extensible category/source model, and are physically removed on delete. The Account page links to `/account/memory`.

Projects now support bounded `Instructions` and `ContextNotes` fields. They are editable on the existing project form and on the project detail page. When a conversation has a matching project, the API includes only that project’s context. It also includes only active memories owned by the current user in the conversation workspace. Project context and memory are inserted into the server-built system instruction; they are never loaded into another user’s conversation. The context builder preserves the existing history trimming and reserves separate budgets for project context, memory, and output (`ContextBudgetTokens=12000`, `ProjectContextBudgetTokens=1200`, `PersonalMemoryContextBudgetTokens=1200`, `ContextOutputReserveTokens=2048`, `MaxPersonalMemories=50`).

## Batch 3.5 files and attachments

Batch 3.5 adds `StoredFile` metadata and `ChatMessageAttachment` joins. Local development and tests use the safe filesystem provider under `Files:LocalRootPath`; Production selects the AWS SDK-backed `S3CompatibleFileStorageService` for Cloudflare R2 so the API does not silently write user files to ephemeral Railway disk. Uploads validate filename, extension, declared MIME type, signature, and size before bounded TXT, Markdown, PDF, DOCX, CSV, or XLSX extraction. Images remain binary attachments for a provider-capable vision path.

The browser receives safe file metadata and sends opaque attachment IDs. The API enforces workspace, project, conversation, uploader, readiness, and attachment-count checks before resolving selected files server-side. Storage keys, extracted text, binary data, provider credentials, and provider file IDs never enter browser DTOs. See [docs/FILES_ARCHITECTURE.md](docs/FILES_ARCHITECTURE.md) for the storage adapter boundary and production configuration.

## Batch 3.6 Generation Jobs

Batch 3.6 adds the database-backed `GenerationJob` queue, atomic PostgreSQL claim path, configurable single-concurrency worker, independently registered handlers, cooperative cancellation, output provenance, recovery of expired claims, and zero-cost `system.test` handler. The protected `/account/generation-jobs` route remains an internal validation surface rather than a studio. See [docs/GENERATION_JOBS_ARCHITECTURE.md](docs/GENERATION_JOBS_ARCHITECTURE.md).

## Batch 3.7 unified Assets

Batch 3.7 adds `Asset` as the reusable product layer above private `StoredFile` metadata and immutable `GenerationJobOutput` provenance. Successful `system.test` execution writes a deterministic JSON artifact through the configured storage adapter, links the output, and publishes one Asset in the same workspace and optional project. Failed or cancelled jobs publish no Asset, and system-test usage remains zero.

Authenticated `/api/assets` routes support paginated listing, text/type/project/status filters, get, metadata updates, project assignment/removal, archive, restore, and authorized private-file streaming. `/assets` provides the localized English, Arabic, and Kurdish Sorani product library, while project details show project-assigned Assets. See [docs/ASSET_ARCHITECTURE.md](docs/ASSET_ARCHITECTURE.md) for extension rules future handlers must follow.

## Batch 3.8 Image Studio

Batch 3.8 registers `image.generate` on the durable Generation Job worker and adds `POST /api/image-generation/jobs`. The protected `/create/image` page collects a bounded description plus structured style, aspect ratio, quality, project, mood, background, title, and exact-text controls. It polls the existing job snapshot API, displays progress and safe failures, supports cooperative cancellation, and shows a private Asset preview after success. Successful image jobs create one `StoredFile`, one `GenerationJobOutput`, and one reusable `image` Asset; failed or cancelled jobs publish none.

The API uses the server-only OpenAI Images adapter configured for `gpt-image-2.5-sunburst`. Provider/model selection, prompts after enrichment, credentials, storage keys, raw provider payloads, and moderation details never enter browser responses. The existing zero-customer-charge usage service records `UsageFeature.Image` and configured provider cost metadata for internal accounting. Reference-image editing remains explicitly deferred. See [docs/IMAGE_STUDIO_ARCHITECTURE.md](docs/IMAGE_STUDIO_ARCHITECTURE.md).

## Batch 3.9 accounting metadata fix

The follow-up accounting fix completes the authoritative Image Studio ledger path. New successful image transactions persist the configured pricing version and immutable rate snapshot, provider latency measured around only the OpenAI request, currency, cost basis (`Actual` or `Estimated`), standard OpenAI input/output quantities, optional image-specific detail quantities, provider/model, and Generation Job provenance. The Images API currently reports billable image generation usage through standard `input_tokens` and `output_tokens`; Taslim leaves image-specific fields null when the optional detail objects are absent rather than inventing values. Existing historical transactions are not backfilled with unproven metadata. `AddUsageCostBasis` is additive and preserves `DataProtectionKeys`. Admin-only inspection shows the new fields in readable form; ordinary `/account/usage` remains redacted. See [docs/USAGE_ACCOUNTING_ARCHITECTURE.md](docs/USAGE_ACCOUNTING_ARCHITECTURE.md).

## Batch 3.10 Document Studio

Batch 3.10 adds the protected `/create/document` workflow. Users provide a natural-language description, optional document type/length/tone/language/audience guidance, a project, and explicitly selected ready files. The API validates ownership and extraction status, queues `document.generate`, drafts one canonical bounded `DocumentDraft`, and renders that draft to both DOCX and PDF through server-side renderer abstractions. One logical document Asset owns both private representations through the additive `AssetRepresentation` relationship. The completed screen provides an escaped structured preview, secure format downloads, and an Assets link. See [docs/DOCUMENT_STUDIO_ARCHITECTURE.md](docs/DOCUMENT_STUDIO_ARCHITECTURE.md) for the workflow, renderer dependencies, usage accounting, security boundaries, and future extension path.

Document generation remains provider-independent at the handler boundary and reuses the existing server-only AI infrastructure. Production settings enable the feature with bounded context, attachment, output, and timeout limits; no new Railway service or secret is required. QuestPDF native assets and the packaged Arabic font are included in the existing API publish output. Customer charge remains zero and provider/model details remain administrator-only.


## Batch 3.11 Presentation Studio

Batch 3.11 adds the protected `/create/presentation` workflow. Users provide a brief, presentation type, length, tone, language, optional audience and brand details, an optional project, and explicitly selected ready extracted PDF, DOCX, TXT, MD, CSV, or XLSX files. The API queues `presentation.generate`, produces a strict canonical `PresentationDraft`, and renders one editable 16:9 PPTX through the managed Open XML SDK. One logical presentation Asset owns the private PPTX representation. PDF conversion is intentionally deferred because no reliable managed PPTX-to-PDF runtime was validated for the production Linux deployment; the UI exposes only representations that actually exist. See [docs/PRESENTATION_STUDIO_ARCHITECTURE.md](docs/PRESENTATION_STUDIO_ARCHITECTURE.md).

## Batch 3.12 Research Studio

Batch 3.12 adds the protected `/create/research` workflow. Users ask a research question, choose depth and report type, select a language, optionally provide audience/geography/time constraints and domain preferences, choose a project, and explicitly select extracted source files. Current web research uses the server-side OpenAI Responses API `web_search` tool and normalizes returned URLs, titles, domains, retrieval metadata, and citation annotations into job-scoped ResearchSource and ResearchEvidence records. The report provider must return strict structured `ResearchDraft` JSON with validated source IDs; unknown citations, malformed output, oversized context, or provider failure produce safe terminal job errors rather than an uncited or fabricated report.

Successful Research jobs create one private `research` Asset with DOCX and PDF representations through the existing document renderer. The result preview shows the executive summary, key findings, cited sections, evidence excerpts, and safe clickable source links. Research usage is recorded through `UsageFeature.Research` with zero customer charge; provider/model/cost details remain administrator-only. No Personal Memory or all-project-file injection occurs. See [docs/RESEARCH_STUDIO_ARCHITECTURE.md](docs/RESEARCH_STUDIO_ARCHITECTURE.md) and [OpenAI web search documentation](https://developers.openai.com/api/docs/guides/tools-web-search).

## Batch 3.13 Social Studio

Batch 3.13 adds the protected `/create/social` workflow. Users describe what they want to share, choose a platform, content type, tone, and language, and optionally add audience, brand voice, call to action, variants, a project, selected extracted source files, and selected existing Assets. The API queues `social.generate`, produces a strict canonical `SocialDraft`, and publishes one private JSON-backed `social` Asset through the existing durable Generation Job and Asset pipeline. The completion view renders each post, hook, body, CTA, hashtags, alt text, and visual direction with a local copy action.

Social Studio uses no automatic Personal Memory, all-project-file injection, Image Studio calls, social credentials, or direct publishing APIs. Selected sources and Assets are explicit, bounded, and workspace-authorized. Internal provider/model/usage details remain administrator-only; customer charge remains zero through `UsageFeature.Social`. No schema migration is required. See [docs/SOCIAL_STUDIO_ARCHITECTURE.md](docs/SOCIAL_STUDIO_ARCHITECTURE.md).
