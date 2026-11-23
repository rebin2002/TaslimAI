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
| `AcademicSourceConnectionPolicy.cs` | Connection-layer SSRF boundary: resolved-address screening, per-connection IP pinning, pinned `SocketsHttpHandler`. |
| `AcademicHttpGateway.cs` | Shared transport: per-request deadlines, manual redirect revalidation, cross-authority credential gate, bounded JSON reads, `Retry-After` handling. |
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

Two independent layers, because a URL check and an address check answer
different questions.

**Layer 1 — endpoint allow list (`AcademicSourceUrlGuard`).** Provider base
URLs are configuration, never request input, and their hosts form an
**exact-host** allow list enforced before every read and re-enforced on every
manual redirect hop (`AllowAutoRedirect` is disabled on the registered
clients). A configured `openlibrary.org` admits `openlibrary.org` and nothing
else: sub-domain breadth is not implied by an allow-list entry. The only way to
admit breadth is `EndpointHostExceptions`, an explicit operator list, and
`AllowCrossHostRedirects` stays off so a provider-issued `Location` cannot move
a read to a different allow-listed host either. Credentials and fragments are
rejected in URLs, and IP literals — including bracketed IPv6 such as
`[::1]`, which must be un-bracketed before it can be classified — are rejected
unless they are exact allow-list members **and** in public routable space.

**Layer 2 — resolved-address screening and pinning
(`AcademicSourceConnectionPolicy`).** Layer 1 cannot see what a hostname
resolves to, so an allow-listed host whose DNS record points at
`169.254.169.254` or `127.0.0.1` would otherwise be dialled. Before a socket is
created the policy resolves the host and requires every returned address to be
public: loopback, RFC1918, link-local including the cloud metadata address,
CGNAT (`100.64.0.0/10` across its whole range), documentation, benchmarking,
multicast/reserved, IPv6 loopback/link-local/unique-local/site-local,
discard-only `100::/64`, `2001:db8::/32`, 6to4 `2002::/16`, v6-only loopback
`3ffe::/16` and v4-mapped variants are refused. An empty answer is a refusal,
not a pass-through, and a mixed answer (one public record beside one internal
record) refuses the target instead of quietly dialling the convenient one.
The socket is then dialled at **exactly** that validated address, so the
resolver cannot be asked a second time between the check and the connect.

Because redirects are followed manually as separate requests, every hop opens
its own connection and therefore gets its own resolve-verify-pin decision; a hop
to a new host is re-resolved rather than inheriting the previous hop's pin.
TLS is still performed by `SocketsHttpHandler` on top of the pinned stream using
the **original request hostname**, so SNI and certificate name validation are
unchanged — the module installs no `RemoteCertificateValidationCallback`, no
`TargetHost` override and no `HttpClientHandler` anywhere, and a unit test
asserts those stay true. Cookies are off (`UseCookies = false`).

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

## Credentials and redirects

The Google Books key rides the query string because the volumes API has no
header auth. That makes a redirect a credential-leak path, so:

- A hop to a different authority (scheme, host **or** port) is refused unless
  `AllowCrossHostRedirects` is turned on deliberately.
- When it is turned on, the hop target is rebuilt without credential-bearing
  query parameters (`key`, `apikey`, `token`, `access_token`, `secret`,
  `client_secret`, `subscription`, `code`, `sig`, case-insensitive). Legitimate
  parameters survive.
- A hop that would leave the authority is refused outright when the composed
  client carries an `Authorization` or `Cookie` default, because `HttpClient`
  merges those into every send and the gateway cannot strip them per request.
- Same-authority hops (normal provider behaviour such as
  `/works` → `/works-v2`) are left exactly as the provider specified.

## Wiring (integration step, deliberately not part of this PR)

```csharp
// Program.cs, alongside the other feature registrations:
builder.Services.AddAcademicSourceDiscovery(builder.Configuration);
```

Configuration section (`appsettings`):

```json
"AcademicSourceDiscovery": {
  "Enabled": false,
  "OpenAlexEnabled": false,
  "CrossrefEnabled": false,
  "OpenLibraryEnabled": false,
  "GoogleBooksEnabled": false,
  "GoogleBooksApiKey": null,
  "ContactMailto": "ops@example.com",
  "EndpointHostExceptions": [],
  "AllowCrossHostRedirects": false
}
```

Every switch defaults to **off**, and the block above is the default shape: a
deployment that registers the feature but writes no section performs no outbound
calls and reports each provider as disabled, rather than silently starting to
crawl four public APIs. Enabling discovery is an explicit, per-provider
decision. `AddAcademicSourceDiscovery` also registers options validation, so a
base URL that is not absolute http(s), embeds credentials, or points at a
non-public address fails at options resolution instead of at request time;
the same applies to an `EndpointHostExceptions` entry with a single label, which
would otherwise admit every domain under it.

Google Books participates only when both `GoogleBooksEnabled` is true and a key
is configured through environment/secret settings — never committed.

## Contract handoff to later phases

- `AcademicAccessLevel` here is the four-rung ladder
  `Metadata → Abstract → FullTextIndicated → FullTextContent`. A wiring or
  Phase C consumer must **not** collapse `FullTextIndicated` into a plain
  "full text" value: it records that the provider advertised a readable
  location, never that Taslim fetched the document. Merging this phase with a
  three-value access enum loses that distinction, so the wiring PR should map
  `FullTextIndicated` to its own rung rather than to `FullText`.
- `AcademicSourceRecord` in this folder is intended as the single identity
  vocabulary for the wiring PR (`ProviderKey`, `ProviderWorkId`, `Doi`,
  `Isbn13`, `Isbn10`, ...). A second record shape in another phase should be an
  adapter over this one, not a parallel model, so DOI/ISBN dedup keys stay
  comparable across providers.
- Nothing in this PR calls `AddAcademicSourceDiscovery`; that call, the
  `AcademicSourceDiscovery` config section and the research-studio consumer all
  belong to the wiring PR. Until then the feature is present but dormant.

## Tests

`apps/api.Tests/AcademicSources/*` — fixture-driven, mocked-HTTP, no live
network in tests, covering normalization, access-marker integrity, dedup,
pagination bounds, retry/rate-limit behavior, cancellation, SSRF blocks, and
orchestrator failure isolation. The SSRF cases are deterministic by
construction: `IAcademicHostResolver` is injected with a fixed answer table and
the transport is a recorder that throws rather than dialling, so the rebinding,
IPv4/IPv6, private/link-local/CGNAT/metadata and pin-selection matrices run
without DNS or sockets, and each block asserts that zero traffic left the
gateway.
