# Taslim Presentation Studio Architecture

## Scope and product boundary

Batch 3.11 introduces the protected `/create/presentation` workflow for producing an editable PowerPoint presentation. The required representation is a real `.pptx` file. The feature is provider-independent at the job-handler boundary and reuses Taslim’s durable Generation Job queue, private StoredFile layer, Asset library, AI Core structured-output path, workspace authorization, CSRF protection, and zero-customer-charge Usage Ledger.

The product deliberately does not expose provider names, model identifiers, routing controls, prompts after enrichment, storage keys, pricing, token quantities, or internal worker diagnostics to ordinary users. The interface asks for the presentation goal in natural language and provides bounded controls for type, length, tone, language, project, selected extracted sources, audience, title, brand/company, and additional instructions.

## Request and lifecycle

The browser submits a CSRF-protected request to `POST /api/presentation-generation/jobs`. The controller normalizes and validates the request, checks that the workspace and optional project are authorized through the existing Generation Job service, runs the existing usage preflight guardrail with `UsageFeature.Presentation`, and creates `presentation.generate`. The worker claims the job through the existing database-backed queue and executes the registered `PresentationGenerationJobHandler`.

The handler performs the following stages:

1. **Validation.** Request limits, supported types, lengths, tones, languages, and attachment count are checked again from durable job input.
2. **Selected-source authorization.** Only ready, extracted files in the job workspace are loaded. Supported source extensions are PDF, DOCX, TXT, MD, CSV, and XLSX. No project-wide file injection and no Personal Memory injection occur.
3. **Context assembly.** Selected source text and bounded project name, instructions, and context notes are passed through `IPresentationPromptBuilder`. The builder enforces a total context bound and marks source boundaries.
4. **Provider generation.** `IPresentationGenerationProvider` calls the existing `IChatCompletionService` with a strict `AiStructuredOutputSpec`. The handler never performs provider HTTP calls.
5. **Draft validation.** The provider response is parsed as `PresentationDraft` and checked against slide, block, text, row, metric, source-reference, ordering, and markup limits.
6. **PPTX rendering.** `IPresentationRenderer` renders the canonical draft to one managed OOXML package. It uses no image-provider calls and no native conversion runtime.
7. **Publication.** The existing `GeneratedAssetPublisher` writes the private output, creates one `GenerationJobOutput`, and prepares one logical `presentation` Asset. The worker attaches one `pptx` AssetRepresentation and atomically transitions the job to `Succeeded` only after publication and usage completion succeed.

The UI treats the job snapshot as authoritative. It polls until `Succeeded`, `Failed`, or `Cancelled`, retries transient polling errors with bounded backoff, caps visible nonterminal progress at 99%, and treats a successful job without a safely parseable result or Asset ID as `completed-unavailable` rather than falsely presenting a downloadable result.

## Canonical PresentationDraft

The AI boundary is a strict JSON object with title, nullable subtitle, language, theme, presentation type, and an ordered slide list. Each slide contains order, supported slide type, title, nullable subtitle, bounded content blocks, nullable notes, nullable visual suggestion, and source references. Supported slide types are title, agenda, section, content, bullets, two-column, comparison, metrics/KPI, table, timeline, process, quote, summary, next steps, and closing.

Blocks are intentionally structural rather than arbitrary markup. Supported blocks include text, bullets, columns, table rows, metrics, timeline/process items, and quote text. The validator rejects unsupported types, excessive content, malformed rows or metrics, and angle-bracket markup. It does not reverse Unicode strings: direction is represented in OOXML paragraph properties so Arabic and Kurdish text remains semantically correct and editable.

## PPTX renderer

The managed renderer creates a 16:9 presentation package with a presentation part, slide master, blank layout, theme, slide relationships, and one slide XML part per canonical slide. It uses a restrained navy, blue, teal, and whitespace-oriented theme. Text is stored in editable DrawingML runs. Cards, accent bars, metric panels, process/timeline cards, and table cells are editable shapes or editable DrawingML tables. Slide numbers are placed in a consistent footer position.

For Arabic and Kurdish Sorani, the renderer emits right-to-left paragraph alignment and `rtl="1"` markers, uses Arabic-capable font markers, and leaves Unicode content in its original order. Table text also carries RTL column and paragraph metadata. The package is stored privately and downloaded only through the existing authenticated Asset representation endpoint.

Charts are not automatically generated in this batch. A future chart model can be added as a canonical block with validated data and an editable chart renderer. Until then, KPI and chart-like requests use explicitly supplied metrics or table/shape fallbacks; the system never invents numeric values.

## Sources and project context

A user must explicitly select source files. The server rechecks workspace ownership, ready status, extraction status, and supported extensions before using extracted text. Selected project instructions and context notes may be included within configured bounds. Taslim does not inject all project files, unrelated workspace files, or Personal Memory into a Presentation Studio request.

## Output, Asset, and PDF policy

A successful request produces one private PPTX StoredFile, one GenerationJobOutput, one `presentation` Asset, and one `pptx` AssetRepresentation. Asset metadata contains only useful non-sensitive product fields such as presentation type, slide count, language, format, and generation timestamp. Result JSON contains a safe title, language, slide count, bounded preview slides, and post-publication asset/representation descriptors. Provider, model, storage, prompt, and pricing fields are not part of the user result.

PDF is explicitly deferred in Batch 3.11. The managed OpenXML renderer creates PPTX but does not provide a reliable server-side PPTX-to-PDF conversion engine compatible with the production Linux runtime. Taslim therefore does not fake a PDF, does not add LibreOffice or another heavy native dependency, and does not show a PDF action unless a future verified PDF representation exists.

## Usage and failure boundaries

The durable Usage Ledger records `UsageFeature.Presentation`, Generation Job provenance, provider/model, token metadata, latency, cost basis, and pricing snapshot for administrator accounting. The customer charge remains zero through the existing safe charging service. Ordinary user DTOs do not expose provider/model or internal ledger fields.

Validation, provider, draft, rendering, private-storage, asset-publication, cancellation, and worker failures map to Presentation-specific safe codes and messages. The worker discards partial private artifacts on failure or cancellation and does not create a partial Asset. Internal logs contain only job ID, stage, safe code, exception type, provider failure category/status/code where already supported, and elapsed milliseconds. Prompt text, selected source contents, response content, credentials, storage keys, and secrets are not logged.

## Security and authorization

The route requires authenticated access and the existing antiforgery header. Workspace, project, source-file, job, Asset, and representation authorization remains delegated to existing services and controllers. Downloads use credentialed authenticated requests. The new feature does not alter authentication cookies, CORS, CSRF, Data Protection, R2/private storage, Chat, Image Studio, Document Studio, or the database schema.

## Future extension points

Future batches may add explicitly selected image Assets as visual references, an opt-in visual placement model, a validated editable chart model, speaker-note editing, and verified PDF conversion. Those extensions must preserve the canonical-draft boundary, explicit user selection, bounded context, private Asset authorization, atomic publication, and no-invention rule.
