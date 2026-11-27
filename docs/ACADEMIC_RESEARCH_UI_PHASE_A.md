# Academic Research Studio — UI Phase A Architecture

Issue: #1259 (master's and doctoral research upgrade). This slice is Phase A of the four
phases the issue describes, and it is deliberately file-disjoint from PR #1258.

## Decision

Phase A ships the academic surface only: the entry choice, the brief that captures what a
programme requires, a manually verified source library with normalised citations, a manual
evidence matrix, and a level-appropriate chapter plan. It performs **zero network calls** and
starts **zero generation jobs**. Everything the issue assigns to the academic backend —
authoritative scholarly and ISBN discovery, screening, extraction, chapter drafting,
verification and document delivery — is represented as a labelled workflow status, never as
a control.

The route is `/create/research/academic`, a sibling of the existing `/create/research`
surface rather than a modification of it. Standard research behaviour is untouched, so a
business customer's flow cannot be affected by this work.

The rule the whole slice is built around is the issue's integrity rule: the product may only
claim what actually happened. A source the student typed is shown as *recorded by you*; a
record whose retraction status nobody has checked says so; a citation with a missing page
range lists the missing element instead of inventing one; a stage that cannot run yet has no
button.

## Ownership and file boundaries

| Area | Files | Owner |
| --- | --- | --- |
| Academic components | `apps/web/src/components/academic/*` | Phase A |
| Academic route | `apps/web/src/app/create/research/academic/*` | Phase A |
| Standard studio | `components/ResearchStudioView.tsx`, `lib/researchStudioState.ts` | PR #1258 (untouched here) |
| Draft persistence | `lib/sessionDraftScope.ts` | reused read-only |
| Generation jobs, discovery, exports | `apps/api/*` | not touched; no academic endpoints exist yet |

No existing file is edited by this PR. That is the point: Phase A can be reviewed, reverted
or rebased without touching the concurrent studio work.

## Module map

| Module | Responsibility |
| --- | --- |
| `academicCapabilities.ts` | The capability and workflow-stage registry. Every Phase A surface must resolve to a known capability; an unknown id fails closed to `unavailable`. This is the single source of truth behind "no fake buttons". |
| `academicBrief.ts` | The brief model and catalogues: level, discipline, methodology, citation style, source type, university requirements, plus validation and the plan summary rows. |
| `academicSourceLibrary.ts` | Source records, DOI and ISBN validation, dedupe identity, library filtering, integrity flags, and the five citation formatters. |
| `academicStudioState.ts` | Tabs, chapter plan and templates, evidence notes, and the identity-scoped draft read/write/clear helpers. |
| `academicStrings.ts` | The Phase A copy dictionary (`en` complete, `ar`/`ku` partial), `asKey`, the translator and the option labelers. Feature-local so this PR never touches the owner-claimed shared `i18n.ts`/`navigation.ts`. |
| `AcademicPrimitives.tsx` | Badge, notice, and field building blocks, including the `data-academic-field` anchors the readiness list jumps to. |
| `AcademicTabs.tsx` | The accessible tablist: roving focus, arrow/Home/End with RTL mirroring, real `tabpanel` semantics. |
| `AcademicResearchStudio.tsx` | The container that owns workspace state and wires the four tabs to the panels. |

## Domain rules

### Brief and discipline catalogues

Disciplines, methodologies and citation styles are closed union types. Choosing a discipline
offers its common practice — typical methods, typical style, typically accepted source types —
but only through the explicit *Use common practice for this discipline* action, so switching
discipline never silently rewrites answers the student already gave. The programme's own
handbook rules always win: `summarizeBrief` echoes what the student recorded and labels it as
theirs.

Validation reports *blocking* and *advisory* issues separately. A missing research question or
methodology blocks the plan; a word limit that has not been recorded does not — plenty of
programmes publish no number.

### Identifier validation

- **DOI**: shape only (`^10\.\d{4,9}/\S+$`), normalised to lowercase with any resolver prefix
  removed. A well-formed DOI means the string is a plausible identifier, *not* that a
  publication with that DOI exists; the UI says "recorded by you", never "verified against a
  registry", because nothing was fetched.
- **ISBN**: checksum-validated (ISBN-10 converted to ISBN-13 digit form, weights applied,
  check digit confirmed). Normalisation emits digits only; no hyphenation pattern is invented.
- **Dedupe identity**: `doi:` → `isbn:` → `title:…|year`. A duplicate is reported with the
  label of the record already in the library instead of being added twice.

### Citation formatting

Five styles ship in Phase A: APA 7, Harvard, Chicago, IEEE and Vancouver. Each formatter
returns `{ style, text, missing[], complete }`. Missing elements are reported by name
(`authors`, `year`, `title`, `container`, `publisher`, `volume`, `identifier`) and skipped in
the output. A formatter never synthesises a page range, an edition statement, an author, or a
container title. Long author lists truncate with the style's own convention (`et al.`) rather
than being dropped silently.

`access` is a first-class field (`metadata`, `abstract`, `full_text`, `pdf_offsite`) because
"we found a record" and "the student read the work" are different claims, and the issue
requires the difference to survive to the UI.

### Integrity flags

`sourceIntegrityFlags` marks a retracted work, an unchecked retraction status, a
metadata-only record being cited as if read, and a record with no verified identifier at all.
Flags are advisory text next to the record; they never delete or hide a source the student
entered.

### Workflow stages

The eight stages from the issue (`plan → discovery → screen → extract → compare → gaps →
chapters → verify`) render with a status badge: exactly one is `available` today (planning,
with the manual source library and evidence matrix), the rest say *Coming in a later step* and
carry their delivery phase. The badge is the only control-free affordance in the slice.

## Honesty and privacy guarantees asserted by tests

1. **No fake controls.** The composition test enumerates every `<button>` rendered on all four
   tabs against an allowlist of controls that genuinely do something locally (tabs, save
   draft, start over, discipline defaults, add/remove a source or sub-question, jump to a
   blocked field). Nothing that would start a job, generate text or export a document exists.
2. **No invented content.** A source with missing elements shows the missing list and no
   fabricated values; no test fixture's absent field appears in the rendered citation.
3. **No provider, model or cost disclosure.** The rendered text is scanned for provider names,
   model names, tokens, credits and currency. Phase A talks to no provider, so there is
   nothing to leak; the scan keeps it that way.
4. **Private by default.** Drafts persist through `sessionDraftScope`, so the storage key is
   scoped to the authenticated user and workspace; the tests prove one identity cannot read
   another's draft and that a cleared draft is gone. The route is `force-dynamic`, gated by
   `requireAuthenticatedPage`, rendered inside `ProtectedPage`, and marked
   `noindex, nofollow, noarchive`.
   Scoping the key is not sufficient on its own: a workspace that is already mounted keeps its
   in-memory state across a principal change unless something destroys it, and the auth
   provider does not key its children. The container therefore renders the workspace inside a
   subtree keyed by `(user, workspace)`, so an identity change unmounts it — nested input
   buffers included — and the workspace fails closed on its own as well: it records the scope
   its state belongs to, drops that state the moment the scope moves, refuses a host seed that
   belongs to another scope, and routes every mutation, save and clear through one ownership
   check. Ownership is decided from the scope the object carries rather than its reference, so
   a session refresh returning the same student does not discard that student's unsaved work.
   `e2e/academic-identity-privacy.spec.ts` mounts the real component in a browser and switches
   the principal underneath it: a next student with no draft, a returning student's own draft,
   sign-out, rapid switching, and an equal-scope re-render.
5. **Recovery without leakage.** The route `error.tsx` boundary announces with
   `role="alert"`/`aria-live="assertive"`, restores focus, offers retry and an Assets exit, and
   never renders `error.message` or `error.digest`, either of which can carry brief text.
6. **Real labels only.** No control relies on a placeholder as its accessible name, every
   `for=` resolves to a rendered control id, and no raw dictionary key reaches the DOM.

## Accessibility and responsive behaviour

- Tabs: `role="tablist"`, one `aria-selected` tab, roving tabindex, Arrow/Home/End movement
  mirrored for RTL, and a `tabpanel` labelled by its tab so it is reachable by Tab.
- Fields: `aria-required`, `aria-describedby` wiring hints and errors, grouped radio and
  checkbox sets inside labelled `role="group"` containers, and visible focus rings.
- Evidence matrix: a real `<table>` with `<caption>`, `scope="col"`/`scope="row"` and a
  textarea per cell keyed to the student's own sub-question.
- Layout: a single-column flow under 900px, wrapped filter and header bars under 620px,
  44px minimum tab target height, `dir="rtl"` overrides for chips and the allocation bar, and a
  `prefers-reduced-motion` guard.

## Testing approach

The workspace has no jsdom or testing-library, so components are rendered with
`react-dom/server` `renderToStaticMarkup` and asserted as text and markup — the convention the
existing studio tests already use. Domain logic (validation, dedupe, citation, state, drafts)
is pure and deterministic, so it is tested directly. Provider-mocked composition tests pin the
honesty guarantees above. `page.test.tsx` pins the auth contract from the route module's
source, matching `src/app/create/research/page.test.tsx`.

134 academic tests across 10 files. Full workspace suite: 1679 tests, 264 files, green.

Mounted-identity behaviour is invisible to static markup, so it has its own browser
regression. `e2e/academic-identity-privacy.spec.ts` builds the studio with the vite that
already ships with vitest — no new dependency, no web server, no API: the academic surface
makes no network call and the bundle is served to Chromium through request interception — and
drives the principal through the host override the component documents. Synthetic data only.
It runs inside the existing `npm run test:e2e` job.

## Follow-up integration (deliberately not in this PR)

The one small navigation link into the academic surface belongs in
`components/ResearchStudioView.tsx`, which PR #1258 is currently editing. Adding it here would
create a conflict on a file this slice does not own. After #1258 merges, the follow-up is:

```tsx
// components/ResearchStudioView.tsx — next to the existing studio actions
import { academicRoute } from "@/components/academic/academicRoutes";

<Link href={academicRoute} className="secondary-button">
  {t("research.academicEntry")}
</Link>
```

plus the matching `research.academicEntry` key in `lib/navigation.ts` / `lib/i18n.ts`, which
Phase A avoids touching because those files are owner-claimed. The current implementation uses
`NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED` as a shared public build-time visibility gate instead:
only the exact value `true` shows the Standard Research link or permits the signed-in
`/create/research/academic` and `/create/research/academic/export` pages to render. The default is
OFF; when unset or false, both direct routes return not found after authentication. This switch
does not replace authentication or enable scholarly discovery, external services or charging.
CI opts in only for synthetic Browser E2E. Production visibility and cutover remain separately
reviewed and authorized.

## Language coverage

`en` is complete for Phase A. `ar` and `ku` carry the headings, actions and status copy, and
fall back to English for the long explanatory strings until the language-pack pass
(`LANGUAGE_PACK_ARCHITECTURE.md`). `academicLocaleCoverage()` reports the ratio so a later
test can fail if coverage regresses, and the fallback is visible rather than silent.
