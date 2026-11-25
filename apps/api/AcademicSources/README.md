# Academic source discovery (Phase A)

Backend scholarly discovery adapters for the Academic Research Studio upgrade
(issue #1259, Phase A). This folder is deliberately self-contained and
file-disjoint from the standard Research Studio pipeline: nothing here imports
`ResearchGenerationJobHandler`. `Program.cs` is no longer untouched — this slice
registers the feature (see Wiring below) — but registration is not activation:
every switch still defaults to off.

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

Redirects are followed manually, so a hop is another send: one logical provider
call is at most `1 + MaxRedirectHops + (MaxAttempts - 1)` = 6 sends with the
defaults, and each of those is a separate resolve-verify-pin connection.
A provider header that cannot be resolved into an address — `Location: //`,
`Location: //:80/x` — is refused as `malformed_redirect` rather than escaping as
an unlabelled HTTP 500, and its response is released before the resolution is
attempted, so the malformed header cannot hold a payload stream or a pooled
connection.

### Provider key quota — arithmetic to settle before activation

`RateLimiting.Search` bounds a *caller*: 120 permits per minute per
`user:<id>` (or per client IP before authentication) plus a concurrency limit of
8 in-flight searches per the same partition. Nothing in this build bounds what a
caller may cost a *provider key*, so with the shipped defaults the arithmetic an
operator has to compare against the quota of the project that owns
`GoogleBooksApiKey` is:

| Shape | Key-bearing calls |
| --- | --- |
| One search term (Google Books is registered as both a work-search and a book-catalog provider, so it answers twice) | ≤ `MaxPages` × 2 = **4** |
| One request carrying ISBN lookups as well | + ≤ min(`MaxIdentifierLookups`=25, accepted list=10) = ≤ **14** total |
| Worst case with redirects and retries | ≤ 14 × 6 = **84 sends** |
| One user, one minute, at the 120/min ceiling | ≤ 14 × 120 = **1,680 calls** (10,080 sends) |
| One user, one day, at that rate | ≤ **2,419,200 calls** |
| Whole deployment | above × number of concurrently active users; the limiter is per user, so total fan-out scales with usage and is **not** capped anywhere |

Consequences to accept or mitigate **before** setting `GoogleBooksEnabled` to
true in a deployment, since none of them is enforced in code:

1. There is no daily or per-key budget in this slice: no counter, no reservation,
   no circuit breaker on quota exhaustion. A 403/429 from Google surfaces as a
   provider outcome category and degrades the batch to a partial result; it does
   not stop the next caller from spending more of the same key.
2. One key is one quota shared by every workspace. The per-user limiter protects
   each student from *another student's* click rate only in the sense that it
   bounds that student; it does not protect the key from the sum of students.
3. Mitigations available to an operator, in the order they should be considered:
   keep `GoogleBooksEnabled` false (the shipped default) and run on the keyless
   catalogs, which need only `ContactMailto` for polite pooling; set a project
   quota ceiling on the Google Cloud side below the contract's allowance; lower
   `MaxPages` (each page is a call) or `MaxResultsPerProvider`; or serve Google
   Books through a caching/egress proxy with its own budget. A per-key daily
   budget enforced inside this service would be the complete answer and belongs
   to a later phase — it is deliberately not approximated here.
4. Whatever is chosen must be recorded against the activation decision, not
   inferred from the config: `Enabled`/`GoogleBooksEnabled` default to false, and
   the section written in `appsettings` is the only place the choice lives.

### What paging does not promise

`totalMatched` and `hasMore` are honest **inside one response**: the pool was
fetched once for that request, and the reported window agrees with the reported
count. Across two responses — a page turn, or a retry — the endpoint keeps no
cursor, no session and no cached pool, so page 2 is a second independent search.
A catalog that added a record, re-ranked the topic, or answered differently this
time can hand back a page that repeats an item the reader already saw or omits
one they had not. The studio says this in `sources.findLimits`; a stable snapshot
would need a server-side cursor, which this slice does not claim.

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

## Wiring (performed by this slice)

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

The call above is in `apps/api/Program.cs` and `Controllers/AcademicSourcesController.cs`
is mapped, so the endpoint exists in the build and the studio can reach it.
Nothing is fetched until an operator writes the section: with the shipped
configuration the endpoint answers `503 ACADEMIC_SEARCH_UNAVAILABLE` and zero
catalog requests leave the process. Because the pinned-dial transport is now
reachable in production, `UseProxy = false` in
`AcademicSourceConnectionPolicy.CreatePinnedHandler` is load-bearing: the .NET
default (`true`) would let an ambient `HTTP_PROXY`/`HTTPS_PROXY` become the dial
endpoint, and the guard would then classify the proxy hostname rather than the
provider's.

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
- Provider-issued addresses are admitted twice over, for two different
  questions. Persistence uses the shared research predicate (unchanged by this
  slice); display uses `AcademicProviderLinkPolicy`, applied where the DTO is
  built, which additionally refuses machine-local hostnames (`.local`,
  `.internal`, `.test`, `.lan`, `.home`, `.intranet`, `.invalid`,
  `.localdomain`, `localhost` and `.localhost`), single-label names, userinfo,
  and IP literals the guard does not classify as publicly routable — including
  decimal/octal/hex IPv4 and NAT64/6to4/v4-mapped spellings. Neither layer
  claims to stop a hostname that rebinds after the check: server-side DNS
  validation does not pin what the browser resolves when the reader clicks.

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
