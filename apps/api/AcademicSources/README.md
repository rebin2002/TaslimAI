# Academic source discovery (Phase A)

Backend scholarly discovery adapters for the Academic Research Studio upgrade
(issue #1259, Phase A). This folder is deliberately self-contained and
file-disjoint from the standard Research Studio pipeline: nothing here imports
`ResearchGenerationJobHandler`, and `Program.cs` is untouched.

## What is here

| File | Role |
| --- | --- |
| `AcademicSourceModels.cs` | Normalized source records, queries, batch outcomes, access markers. |
| `AcademicSourceDiscoveryOptions.cs` | Bounded configuration (section `AcademicSourceDiscovery`). |
| `AcademicSourceIdentity.cs` | DOI/ISBN normalization, check-digit validation, ISBN-10/13 conversion, dedup keys. |
| `AcademicSourceUrlGuard.cs` | SSRF boundary: endpoint allow list, private/link-local/metadata IP rejection, provider-issued URL safety. |
| `AcademicHttpGateway.cs` | Shared transport: per-request deadlines, manual redirect revalidation, bounded JSON reads, `Retry-After` handling. |
| `OpenAlexWorksAdapter.cs` | OpenAlex works search (abstracts reconstructed from the inverted index). |
| `CrossrefDoiAdapter.cs` | Crossref DOI registration validation and bibliographic normalization. |
| `OpenLibraryBooksAdapter.cs` | Open Library book/ISBN catalog lookup. |
| `GoogleBooksAdapter.cs` | Optional Google Books volumes adapter; inert without a configured API key. |
| `AcademicSourceMerger.cs` | Cross-provider dedup by DOI/ISBN with integrity-preserving field union. |
| `AcademicSourceDiscoveryService.cs` | Orchestrates providers with per-provider failure isolation and partial-result reporting. |
| `AcademicSourceServiceCollectionExtensions.cs` | Composition root, ready for one-line wiring. |

## Integrity rules enforced by types and tests

- A source's `AccessLevel` is only what the provider itself indicated:
  `Metadata` → `Abstract` → `FullTextIndicated`. `FullTextIndicated` means the
  provider advertised a readable location; it never means this service read the
  document. `FullTextContent` is reserved for a later phase that actually
  fetches and stores content.
- Metadata/abstracts are never presented as read books. Open Library lending
  flags (`ia`, `lending_edition`) and Google Books `PARTIAL` previews stay at
  `Metadata`; Crossref publisher links do not create a full-text marker.
- Google Books publisher descriptions land in `PublisherDescription`, never in
  `Abstract`.
- Page ranges and page counts are copied verbatim from the provider that
  reported them. Absent data stays null; article numbers are not turned into
  pages.
- Titles are never invented: catalog rows without a provider title are dropped.
- Identifiers: only syntactically valid DOIs and check-digit-verified ISBNs are
  stored. ISBN-10↔13 conversion is exact; 979-group ISBNs have no ISBN-10.
  Unregistered DOIs are reported as *not registered*, not as citations.
- Exceptions and user-visible categories are generic ("the academic source
  service..."); provider names, payloads, keys and model metadata never enter
  exception messages or `AcademicProviderErrorDetails`.

## SSRF boundary

Provider base URLs are configuration, never request input. Their hosts form the
allow list enforced before every read and re-enforced on every manual redirect
hop (`AllowAutoRedirect` is disabled on the registered clients). Loopback,
private, link-local (cloud metadata, including `169.254.169.254`), CGNAT,
multicast, IPv6 unique-local/link-local/site-local and v4-mapped variants are
rejected for IP literals; credentials and fragments are rejected in URLs.
Provider-issued URLs persisted on records pass the same policy the research
pipeline uses (`ResearchCitationUrlPolicy`). Response bodies are size-bounded
and JSON-typed before parsing.

## Rate limits, retries, budgets

429/408/5xx and transport errors retry within `MaxAttempts`, honoring
`Retry-After` clamped by `MaxRetryDelaySeconds`; 429 after budget exhaustion is
surfaced as a rate-limited outcome, 5xx as an outage. Pages are bounded by
`MaxPerPage`/`MaxPages`, results by `MaxResultsPerProvider`/`MaxCombinedResults`,
identifier lookups by `MaxIdentifierLookups`, and every call by
`RequestTimeoutSeconds`/`OperationTimeoutSeconds` on top of caller cancellation.
OpenAlex/Crossref polite-pool contact registration is via `ContactMailto`.

## Wiring (integration step, deliberately not part of this PR)

```csharp
// Program.cs, alongside the other feature registrations:
builder.Services.AddAcademicSourceDiscovery(builder.Configuration);
```

Configuration section (`appsettings`):

```json
"AcademicSourceDiscovery": {
  "Enabled": true,
  "OpenAlexEnabled": true,
  "CrossrefEnabled": true,
  "OpenLibraryEnabled": true,
  "GoogleBooksEnabled": false,
  "GoogleBooksApiKey": null,
  "ContactMailto": "ops@example.com"
}
```

Google Books participates only when both `GoogleBooksEnabled` is true and a key
is configured through environment/secret settings — never committed.

## Tests

`apps/api.Tests/AcademicSources/*` — fixture-driven, mocked-HTTP, no live
network in tests, covering normalization, access-marker integrity, dedup,
pagination bounds, retry/rate-limit behavior, cancellation, SSRF blocks, and
orchestrator failure isolation.
