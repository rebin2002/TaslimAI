# Taslim Research Studio Architecture

## Scope and product boundary

Research Studio is a protected, asynchronous workflow at `/create/research`. It turns a user question into a reviewable research report rather than a chat response. The product presents a guided form with a question, research depth, report type, language, optional audience and geographic/time constraints, optional domain preferences, optional project context, and explicitly selected extracted source files. The normal user does not choose a provider, model, routing tier, storage location, or price.

The primary report experience is a readable on-screen summary with visible source cards, citation identifiers, evidence excerpts, and clickable source links. Successful jobs produce one logical `research` Asset with DOCX and PDF representations using the existing managed document renderer and private storage path. There is no image generation, automatic image search, social connector, Personal Memory injection, all-project-file injection, or fabricated citation fallback in this batch.

## Production web-search provider

Research Studio uses a provider-independent `IResearchSearchProvider` abstraction. The production implementation is a server-side OpenAI Responses API adapter that calls the hosted `web_search` tool with `tool_choice: required`. This is a real search path, not a mock, scrape, or model-only answer. The official OpenAI documentation describes `web_search` as the current Responses API integration and exposes consulted URLs through `include: ["web_search_call.action.sources"]`, while message annotations provide URL, title, and character-index citation metadata [OpenAI web search documentation](https://developers.openai.com/api/docs/guides/tools-web-search).

The adapter was capability-probed against the configured OpenAI-compatible endpoint during Batch 3.12 implementation. A `gpt-5.5` Responses request returned a completed `web_search_call`, source URLs under `web_search_call.action.sources`, and a message containing direct `url_citation` annotations with title and character indexes. The production request uses `type: web_search`, `search_context_size`, live external access, `tool_choice: required`, and `include: ["web_search_call.action.sources"]`. It omits the optional `filters` object when both domain lists are empty; populated preferences use only the documented `allowed_domains` and `blocked_domains` fields. Production readiness still depends on the Taslim API deployment retaining the existing server-side `Ai__OpenAI__Enabled=true` and `Ai__OpenAI__ApiKey` configuration. If the provider is unavailable, the job fails with a safe Research error and does not manufacture a report or source list.

Domain preferences are passed to the hosted search tool as bounded `allowed_domains` and `blocked_domains` filters. Live external access is enabled for this Research workflow because current information is a product requirement. The UI never receives the provider API key or internal model selection. The server records only safe provider and usage provenance in internal job and Usage Ledger fields.

## Durable lifecycle

Research requests are normalized and validated by `ResearchGenerationContractMapper` and `ResearchGenerationRequestValidator`. The specialized controller verifies the feature flag, authenticated user, CSRF token, workspace/project relationship, input bounds, source-file limits, and Usage Ledger preflight. It then creates a `research.generate` Generation Job and enqueues it through the existing database-backed queue.

The worker claims the job using the established SQLite test path or PostgreSQL `FOR UPDATE SKIP LOCKED` path. The handler reports bounded progress through the existing serialized progress writer and checks cooperative cancellation between planning, search, context, report, rendering, and publication stages. Terminal state remains authoritative: the job is marked succeeded only after private output publication, Asset/representation attachment, result JSON persistence, and usage completion succeed together. Partial publications are discarded on cancellation or failure, and no partial Asset is treated as a successful result.

| Stage | Responsibility | Safe failure family |
| --- | --- | --- |
| Validation | Normalize request, depth, report type, language, source limits, and feature policy | `RESEARCH_REQUEST_INVALID` |
| Planning | Create bounded search queries and topic objectives deterministically | `RESEARCH_GENERATION_FAILED` |
| Search | Call required hosted web search and normalize source URLs/citations | `RESEARCH_SEARCH_UNAVAILABLE` or `RESEARCH_SEARCH_FAILED` |
| Source normalization | Add explicit uploaded files, bound snippets/evidence, and persist source provenance | `RESEARCH_SOURCE_UNAVAILABLE` or `RESEARCH_SOURCE_EXTRACTION_FAILED` |
| Context | Build bounded source/evidence context with clear source boundaries | `RESEARCH_CONTEXT_TOO_LARGE` |
| Report | Generate strict canonical ResearchDraft JSON using AI Core | `RESEARCH_PROVIDER_UNAVAILABLE` or `RESEARCH_GENERATION_FAILED` |
| Draft validation | Reject unsupported blocks, arbitrary markup, unknown citation IDs, and oversized output | `RESEARCH_OUTPUT_INVALID` or `RESEARCH_CITATION_VALIDATION_FAILED` |
| Rendering | Map the canonical draft to the existing managed DOCX/PDF renderer | `RESEARCH_RENDER_FAILED` |
| Publication | Store private files, create one Research Asset and representations, and commit the job | `RESEARCH_STORAGE_FAILED` |

## Canonical research model

The provider must return a strict `ResearchDraft` with a title, optional subtitle, language, executive summary, key findings, sections, conclusion, and source citation IDs. The schema uses `additionalProperties: false` and a closed block vocabulary: `paragraph`, `bullets`, `numbered_list`, `table`, and `key_finding`. Blocks may contain text, bounded list items, bounded table rows, and citation IDs. Arbitrary HTML, executable content, provider markup, or raw URLs are not accepted as report structure.

Citations are identifiers such as `[S1]` and `[S2]`, not URLs invented by the model. The handler validates every citation ID against the server-normalized source set. A factual block without a citation is permitted only for clearly framed synthesis or conclusion text; the prompt explicitly instructs the report provider to distinguish sourced facts from analysis and to state uncertainty. Unknown citations, source IDs from another job, malformed JSON, oversized content, and unsupported block types fail safely.

## Source and evidence lifecycle

Web search source metadata and selected uploaded-file provenance are normalized into `ResearchSource` records scoped to the workspace and Generation Job. Each source stores a citation ID, URL or uploaded-file reference, canonical URL when available, title, domain, publisher, source type, retrieval time, bounded snippet/context, rank, selected status, and safe metadata. `ResearchEvidence` records store bounded evidence excerpts and their source relationship. The source endpoint is separately protected by job/workspace authorization and returns only safe citation, link, snippet, and evidence fields.

Uploaded files use the existing private `StoredFile` and extraction path. Only explicitly selected PDF, DOCX, TXT, MD, CSV, and XLSX files are eligible, and only ready files with ready extracted text are accepted. Files are not loaded merely because they are in a project. Personal Memory is not included. Project instructions and context notes are included only when the user explicitly chooses a project and remain bounded.

The current hosted search adapter receives source URLs and a provider-generated evidence brief with URL annotations. It does not pretend to have fetched arbitrary pages itself. `IResearchContentFetcher` is an explicit extension point currently implemented as a safe pass-through because hosted search has already returned bounded search context. A future connector may implement page retrieval with domain, timeout, robots, content-type, size, redirect, and malware controls without changing the canonical report or Asset contracts.

## Cited report output

The on-screen result parses the server result defensively. It shows the executive summary, key findings, report sections, conclusion, and source cards. Each web source displays a visible citation ID, title, domain, evidence excerpt when available, and a clickable HTTP(S) source link. Uploaded sources are labeled as uploaded sources and are not given fake web links. The UI never renders model-provided HTML; text is rendered as text, and table rows are rendered through fixed React structures.

The saved report is mapped to the existing `DocumentDraft` model. Every report representation includes source identifiers in the text and a final Sources section containing the source title and URL or uploaded-source label. DOCX is editable and PDF is produced by the already-validated managed QuestPDF path used by Document Studio; Research Studio adds no native converter, browser runtime, LibreOffice dependency, or fabricated PDF.

## Storage and Asset model

A successful Research job publishes private generated files through `GeneratedAssetPublisher`. The first output creates one `Asset` with `AssetType = research`, the job as provenance, bounded safe metadata such as report type, language, source counts, and generation timestamp, and a private stored file. The existing worker attaches DOCX and PDF `AssetRepresentation` rows. Result JSON safely contains the Asset ID, title, language, report preview, source cards, and representation descriptors. Provider keys, model keys, storage keys, credentials, raw prompts, raw provider responses, and extracted private source text are not included in normal user DTOs.

The existing authenticated Asset download path remains the only download route. It checks workspace membership, representation ownership, stored-file readiness, and private storage authorization before returning a file. Research source detail access checks the owning Generation Job through the same workspace authorization service before querying source records.

## RTL and multilingual behavior

The form, result preview, and source viewer use the existing locale and `dir="rtl"` infrastructure for English, Arabic, and Kurdish Sorani. The backend preserves Unicode text and does not reverse strings. The report renderer uses the existing document language and RTL font configuration. Citation identifiers remain left-to-right tokens such as `[S1]`, while surrounding Arabic or Kurdish prose follows the document direction. Tests cover Arabic/Kurdish-safe result parsing and the backend canonical language bounds; the shared document renderer already emits RTL paragraph markers for generated DOCX output.

## Usage and accounting

Research work is accounted internally through `UsageFeature.Research` and the existing idempotent `generation:{jobId}` request key. Search and report stage provenance is combined only when both calls use the same configured provider boundary; safe metadata records stage names, provider/model identifiers, source count, and whether a provider cost was returned. If a future implementation introduces a different provider, it must create separate Usage Ledger transactions rather than silently collapsing incomparable cost data.

Customer charge remains zero through `SafeUsageChargingService`. Provider token counts, latency, pricing snapshot, and safe metadata remain available to authorized administrators through the existing admin usage surface. Normal users see no provider, model, route, token, storage, or price internals.

## Failure and security model

Search failure, report-provider failure, citation mismatch, malformed output, rendering failure, storage failure, cancellation, and authorization failure have distinct internal codes and safe user messages. Worker diagnostics include only Job ID, stage, safe error code, exception type, bounded provider failure category/status/type/code/parameter where available, and elapsed milliseconds. They never log the question, source text, response body, URLs from private uploads, credentials, cookies, headers, or storage keys. Provider validation responses are parsed only for HTTP status and the safe `error.type`, `error.code`, and `error.param` fields.

The server validates source ownership by workspace, project membership through the existing Generation Job service, extracted-file readiness, maximum counts and character budgets, domain preference bounds, supported languages, and canonical block limits. Web links are rendered only when they parse as HTTP or HTTPS URLs. All report downloads remain credentialed and private. The provider prompt treats source material as evidence, not instructions, and keeps project context and source boundaries explicit to reduce prompt-injection and cross-source confusion.

## Future extensions

The interfaces intentionally leave room for additional search providers, first-party page retrieval, source quality scoring, claim-level support review, explicit image-search requests, user-selected image Assets, and generated visuals. None of these are automatic in Batch 3.12. Any future image or chart feature must preserve explicit user intent, source provenance, authorization, bounded media handling, and a clear separation between factual data and design embellishment.

Charts should be generated only from canonical numeric data present in sources or explicitly supplied by the user. Until a structured chart model and editable renderer are validated, Research Studio uses fixed report tables and text evidence rather than inventing numbers or rendering opaque chart images. Social, financial, or market connectors should be added as separate provider implementations with their own terms, permissions, rate limits, provenance, and Usage Ledger transactions.
