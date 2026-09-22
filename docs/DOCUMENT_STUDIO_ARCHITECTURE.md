# Document Studio Architecture

## Scope

Batch 3.10 adds the first real Document Studio workflow at `/create/document`. It accepts a bounded natural-language description, optional guided controls, an optional project, and explicitly selected ready source files. The API creates a `document.generate` Generation Job and the existing database-backed worker performs drafting, validation, rendering, storage, and Asset publication asynchronously. No customer billing is activated; customer charge remains zero while internal usage metadata is recorded.

## Request and authorization

The browser submits `POST /api/document-generation/jobs` with a workspace identifier, optional project identifier, description, document type, requested length, tone, output language, optional title and audience guidance, additional instructions, output format, and attachment IDs. The service authorizes workspace membership and validates the project against that workspace before queueing the job. The handler resolves attachment IDs server-side and accepts only files in the same workspace that are ready, successfully extracted, and within the existing bounded file-processing rules. The client cannot select another user’s project or file by changing an identifier.

The production browser continues to use the existing same-origin Next.js `/api/*` proxy. Authentication cookies, CSRF headers, administrator authorization, private storage, and persistent Data Protection keys are unchanged.

## Canonical document content

The AI layer produces a bounded `DocumentDraft` containing a title, summary, sections, headings, paragraphs, bullet and numbered lists, and simple tables. The prompt builder combines only the user’s description and guided settings, explicitly selected extracted source text, and the selected project’s instructions/context. Personal Memory is not injected automatically, and files not named in the request are not silently used. The structured result is validated for section, block, heading, summary, and text limits before any file is rendered.

Provider selection remains behind `IDocumentGenerationProvider`. The current adapter reuses the existing server-side AI completion service with structured JSON mode and an output-token ceiling; OpenAI HTTP details remain outside the Generation Job handler. A provider timeout is bounded by `DocumentGeneration:ProviderTimeoutSeconds`, and provider failures are mapped to safe document error codes.

## Rendering strategy

A single validated `DocumentDraft` is rendered twice: once to editable DOCX through `DocumentFormat.OpenXml` and once to PDF through QuestPDF. The two files therefore share the same generated content and do not require separate AI calls. DOCX output includes title, headings, paragraphs, lists, simple tables, spacing, margins, Unicode text, and RTL paragraph direction. PDF output uses A4 pages, margins, headings, spacing, tables, footer page numbers, and right-to-left content flow for Arabic and Kurdish Sorani.

QuestPDF is packaged as a NuGet dependency and carries its Linux native renderer assets through the existing .NET publish pipeline. The API runtime image installs the required `fontconfig`, FreeType, and HarfBuzz libraries; no new Railway service or browser runtime is required. `NotoSansArabic-Regular.ttf` is included in API output/publish assets and registered when available for deterministic RTL PDF font support.

## Asset and storage relationship

A successful job writes one private `StoredFile` for each requested representation and one `GenerationJobOutput` for each file. The first output creates one logical `Asset` of type `document`; the generated publisher attaches both files to that Asset through `AssetRepresentation` rows. The Asset Library therefore shows one document rather than duplicate DOCX and PDF cards. Authorized users can download the primary file or a representation through authenticated API routes; storage keys and public R2 URLs never enter DTOs.

`AddAssetRepresentations` is additive. It uses a restrictive relationship to `StoredFile`, a cascading relationship from Asset, a unique `(AssetId, RepresentationType)` index, and a unique StoredFile link. The migration preserves the existing Data Protection key mapping and all prior job, Asset, and usage tables.

Successful results expose only the Asset ID, safe title/language/summary, bounded canonical preview sections, and representation IDs/file metadata. The frontend renders the preview as escaped React text, never arbitrary AI HTML. DOCX/PDF downloads and the `/assets` link remain authenticated.

## Job lifecycle and cancellation

The existing lifecycle is Pending → Queued → Running → Succeeded, with Failed and Cancelled terminal paths. Document progress reports validation, source preparation, drafting, rendering, storage, and completion stages. A pending or queued request can be cancelled immediately; a running request receives cooperative cancellation through the existing worker token. Failed and cancelled jobs publish no Asset or representation.

Document-specific safe codes include `DOCUMENT_REQUEST_INVALID`, `DOCUMENT_ATTACHMENT_UNAVAILABLE`, `DOCUMENT_ATTACHMENT_EXTRACTION_FAILED`, `DOCUMENT_CONTEXT_TOO_LARGE`, `DOCUMENT_PROVIDER_UNAVAILABLE`, `DOCUMENT_OUTPUT_INVALID`, `DOCUMENT_RENDER_FAILED`, `DOCUMENT_STORAGE_FAILED`, `DOCUMENT_GENERATION_FAILED`, and `DOCUMENT_CANCELLED`. Internal logs distinguish validation, context, provider, draft parsing/validation, DOCX/PDF rendering, representation storage, Asset publication, and usage-finalization stages with job ID, safe code, exception type, and elapsed time. Browser responses do not contain prompts, raw provider responses, API keys, storage keys, stack traces, or internal provider/model details.

## Usage accounting

Document execution uses `UsageFeature.Document` through the production Usage Ledger. The ledger records Generation Job provenance, provider/model, input and output quantities when available, provider latency, currency, pricing version, immutable pricing snapshot, and actual-versus-estimated cost basis. If a provider succeeds but rendering or storage fails, the usage metadata is retained on the failed transaction; if the provider is never called, provider cost remains zero. The customer charge remains zero. Document transactions are visible to server-authorized administrators at `/account/admin/usage`; normal users receive the existing redacted usage contract.

## Localization and future path

The route and all visible controls are localized in English, Arabic, and Kurdish Sorani with RTL-compatible layout. UI language and generated document language are independent. The representation relationship leaves a future path for document versions, regenerated formats, editing, templates, branding, and additional renderers without replacing the provider or canonical draft layers. Those capabilities are intentionally outside Batch 3.10.
