# Project Revision Fence — Client Integration

Contracts for `apps/web`'s use of the project `ETag` / `If-Match` fence that
`apps/api` already implements. The server-side rules — the stamp format, the fenced
`PATCH`, the monotonic compare-and-set, the wildcard and legacy semantics — are the
ones merged in #929 and hardened in #1256; they are described by the API's own tests.
This document covers the client half, and the two places where the endpoints had to
match what a browser can actually do.

## Why this document exists

The fence was complete on the server and unused by the browser.
`apps/web/src/lib/api.ts` sent no `If-Match`, and `ProjectDetailView`, `ProjectsView`
and `ProjectContextEditor` all saved through that call. Every browser edit therefore
took the API's last-write-wins path: two tabs open on one project resolved into
"whoever saved second is right", and the loser's stale text silently replaced a newer
save that the member had watched succeed. The client integration is the difference
between a fence that exists and a fence that protects.

## Where the revision comes from

`apps/web/src/lib/projectRevision.ts` — `ProjectRevisionSession`, one per **editable
draft**, keyed on the project id it edits.

A session holds a PAIR: the revision token and the exact draft base — the editable
fields (`name`, `description`, `instructions`, `contextNotes`, `type`) — that the token
certifies. The pairing is the whole point.

1. **Capture is verified against the copy on screen.** The first keystroke calls
   `session.noteDraft(seed)` and then `session.begin()`, where `seed` is the copy the
   draft's own local fields were **initialized from** — the row that arrived through the
   overview or the project list, which never carried an `ETag` of its own. `begin` issues
   `GET /api/projects/{id}` and compares the row it reads with that seed. Only a match
   binds the published token; a mismatch raises `RevisionDivergedError` (a conflict)
   and binds nothing.
   *Why the seed and not the live prop:* the component's `project` prop is replaced by
   every other writer's answer. A sibling form's save, or an archive whose answer carries
   content this draft never displayed, updates the prop while the mounted textarea still
   shows the older text. Comparing the row against the **newer** prop finds a match and
   certifies the **older** fields — the same lost update, arriving one layer up. So the
   seed is reported once, when the draft is created, and a draft moves it only where its
   own fields move: a project switch, an explicit reload, or a write this draft made.
   `begin()` and `save()` deliberately take no snapshot argument, so there is no way to
   pass the newer copy by accident.
   *Why a bare token is not enough either:* a stamp read at the moment of the first
   keystroke describes the row as it is **then**. If another tab saved between this page
   rendering and that keystroke, adopting the new token while keeping the older fields
   would certify the stale draft as current. The same applies to clicking Save without
   typing, and to a capture that failed and is retried later: every path binds through
   the same comparison, and a session that has never been told what is on screen binds
   nothing at all.
2. **The token is carried verbatim.** The published format is
   `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` (seven fractional digits, a 100-nanosecond grid).
   JavaScript's `Date` keeps three, so any value rebuilt from `project.updatedAt` would be
   a token the server can never match. The client stores the header string exactly as it
   arrived and never parses it. `api.getProjectWithRevision` returns the header, not a
   derived field.
3. **Advance from the write's own answer.** A successful `PATCH` publishes the revision it
   committed together with the row it left behind, so the session re-binds to that pair
   without a follow-up read. That avoids both an extra round trip and a race in which a
   third writer slips between the write and the re-read.
   When the answer arrives WITHOUT a header (an intermediate that strips it), the two halves
   of that pair are treated differently and deliberately: the **token** is withheld, so the
   next save must re-capture before it may declare anything and refuses if the row still
   publishes nothing, while the **baseline** moves to the content this draft just wrote —
   the member is looking at it, and this session put it there. Holding on to the older
   baseline would make the next capture compare the row against a copy that no longer exists
   anywhere and report a conflict that never happened.
4. **A late answer speaks for the draft that asked for it.** A capture that is still running
   when the member navigates to another project (or the view hands the draft a new session)
   may not set conflict or error state on what is now on screen: the component compares the
   originating project id and session identity before it reports anything, and drops the
   verdict otherwise.
5. **A binding is kept until the copy on screen changes.** The comparison belongs to the
   moment the token is taken, not to every later save: a draft that reloaded the row, or
   that saved while its parent has not yet re-rendered with the answer, is still editing
   exactly the content its token certifies, and a token the row has moved past is caught by
   the server's own fence on the next write. What does drop a binding is the draft itself
   being re-based (`noteDraft` with different content) or a folded-in write that contradicts
   it — and a read that was still in flight when either happened is abandoned rather than
   committed, because its answer describes the moment before the change.
6. **One session per draft, never one per page.** `ProjectDetailView` renders two editable
   drafts of the same row (details and context) and each submits the whole row, so it owns
   two sessions (`"details"` and `"context"`). A token earned by one draft's write must not
   certify the other draft's independently populated fields.

## What each edit path does

| Path | Component | Behaviour |
| --- | --- | --- |
| Project context (instructions / notes) | `ProjectContextEditor` | Fenced `PATCH`; conflict prompt |
| Name, description, type | `ProjectForm` (used by the detail view and the project list) | Fenced `PATCH`; conflict prompt, draft retained |
| Archive / restore | `ProjectDetailView`, `ProjectsView` | Unconditional by design; conditionally adopts the published revision (see below) |

`ProjectsView` owns one session per opened edit row and `ProjectDetailView` one per draft,
so that a status change made by the same view can advance a token that still describes the
row rather than stranding an open draft on a revision the server has already moved past.
Adoption runs through the same comparison as capture: the archived row's **content** must
still equal what the draft reported it is showing. Then the stamp is adopted and the draft
stays savable; if it does not, the token is dropped and the divergence is raised on the
spot, so the next save refuses. A draft that has never reported its content adopts nothing —
an unbound session cannot be certified by someone else's write.

The unfenced `api.updateProject` remains in the module as the legacy transport for any
caller outside these paths; nothing under `apps/web/src` uses it after this change.

## Conflict handling

Two answers produce the same prompt, with different wording:

* the server refused a declared revision — `409 PROJECT_REVISION_CONFLICT`;
* this client found, before attempting anything, that the row no longer matches the copy
  being edited — `REVISION_DIVERGED` (`RevisionDivergedError`), raised while binding.

In both cases:

* nothing of the draft is written (the API applies no fields on a refusal, and a divergence
  never issues a `PATCH` at all);
* the unsaved input stays on screen, exactly as typed;
* the member is shown `projects.revisionConflict` or `projects.revisionDiverged`, localised
  in English, Arabic and Kurdish, with two explicit actions and no automatic retry:
  * **Load the latest saved version** — `session.reloadLatest()` re-reads the row, replaces
    the draft, and re-bases the session on the newly published revision;
  * **Keep my changes and save over the newer version** — `session.saveOverLatest()` sends
    `If-Match: *`, which is the API's documented "apply over the live row" path. This is the
    only way a newer revision is overwritten, and it requires that click.
* Both actions are serialized by an in-flight guard and disabled while either runs. Reload
  and keep-mine both write, and a double click that ran them together would let a wildcard
  overwrite the reload it raced.
* The expired token is never quietly swapped for the live one. A `409` answer carries the
  revision that currently holds the row (`ApiError.revision`, taken from the response's
  `ETag`), but that value is used only for display of, and for the deliberate reload of,
  the newer copy. An unresolved conflict is not an implicit acceptance of either side.
* A divergence latches: continued typing does not re-read the row looking for a usable
  token, and a later save does not quietly go through. It is cleared by the member's choice
  (reload, or the wildcard write) or by dropping the draft.
* A `400 INVALID_IF_MATCH` — which can only be a client defect, since the token did not come
  from this endpoint — drops the unusable token and re-captures on the next save rather than
  replaying a broken header.

## Archive and restore: scope audit

`ProjectsController.Archive` and `.Restore` call the server's `RevisionFence.LiveRow` path:
they deliberately do not inspect `If-Match`, and they apply over whatever the row holds.
That was reviewed and intended on the API lane, and this audit confirms the client does not
need a precondition there:

* A status toggle carries no user-authored content that can be lost, so a "conflict" answer
  would have nothing for the member to reconcile.
* Both endpoints now publish the stamp they committed on their success answer as well as on
  the conflict path (before this change only the `409` carried an `ETag`, so a client holding
  an open draft had nothing to adopt and had to re-read or lose its binding). Both move the
  revision forward monotonically through the atomic compare-and-set fixed in #1256, so an
  archive that lands on top of another writer's commit sits ahead of it rather than reusing
  or rewinding its stamp.
* Adoption is **conditional**, and that is the part that matters for correctness. A status
  answer is folded into a draft's binding only while the row's editable content still
  matches the base that binding certifies. If it does not — the row moved through a change
  this draft never saw, which is exactly what an out-of-band save followed by an archive looks
  like — the binding is dropped, and the draft's next save is refused rather than blessed with
  the archive's brand-new token. A status click must never certify a stale unsaved draft.
* The consequence, stated plainly: a concurrent archive from another tab cannot be refused.
  Archive/restore remain last-write-wins by design. If a future requirement makes concurrent
  archiving user-visible (for example an undo affordance), the precondition belongs on those
  endpoints first and the client follows with `archiveProjectWithRevision` already in place.

The only API-side changes in this lane are ones a browser requires and that weaken nothing:
the `Frontend` CORS policy exposing `ETag`, and the archive/restore success answers publishing
the revision they committed. Their fences, authorization checks and status semantics are unchanged.

## Transport

Production browser traffic is same-origin: the client calls `/api/...`, and
`apps/web/src/app/api/[...path]/route.ts` forwards to the API. That proxy copies every
non-hop-by-hop request header upstream and every response header back, so `If-Match` reaches
the API and the published `ETag` — on both the `200` and the `409` answer — reaches the
browser. No CORS or `Access-Control-Expose-Headers` change is needed for the deployed
topology; the direct-origin path used in development already runs with `AllowAnyHeader`.
Both legs are pinned by tests rather than by inspection.

The other topology — a frontend served from its own origin, which is what local development
and the hosted browser suite use — needed one server change. `ETag` is not a
CORS-safelisted response header, so without `Access-Control-Expose-Headers` a browser on
another origin receives the fenced answer but cannot read the token in it, and a client that
refuses to write unfenced has nothing to declare. The API's `Frontend` CORS policy therefore
now exposes `ETag` (`.WithExposedHeaders("ETag")` in `apps/api/Program.cs`). This widens
nothing: the policy still allow-lists specific origins with credentials, and the exposed
value is a revision stamp of a row the caller is already authorized to read.

When a read still publishes no revision, `ProjectRevisionSession.save` throws
`RevisionUnavailableError` and the views show `projects.revisionLoadError` rather than
writing anyway. A stripped header degrades to a refused save with an explanation, never back
to the silent overwrite this change removes.

Writes remain subject to the API's `[ValidateAntiForgeryToken]` + `X-CSRF-TOKEN` requirement,
and the fenced calls retry a CSRF rejection exactly once, re-declaring the same token — a
retry never converts a conditional write into a wildcard.

## Tests

* `src/lib/projectRevision.test.ts` — session behaviour against the shared server stub:
  capture verified against the draft's seeded copy, two-tab refusal, live revision on the conflict,
  explicit wildcard override, reload re-basing, divergence before the first keystroke,
  no-keystroke save on a stale page, a latched divergence that neither re-reads nor retries,
  a failed capture retried both ways (row unmoved → binds, row moved → refuses), one shared
  read for concurrent beginnings, whitespace equivalence against the server's trimmed copy,
  per-draft isolation on one row, status adoption and its refusal to bless a moved row,
  adoption refused for a draft that reported no content, adoption allowed where the reported
  seed still matches, a status write that landed on content an untouched draft never saw, a
  capture abandoned because the row moved while it was in flight, and a seed reported for some
  other row, the baseline a write keeps when its answer arrives without a header, and the
  re-capture that follows it,
  no re-read per save, no unfenced write, cross-project token isolation, verbatim
  100-nanosecond precision, legacy call silence.
* `src/lib/projectRevisionFenceFixtures.ts` — the stub of the fenced endpoint (including
  `400 INVALID_IF_MATCH`, archive/restore stamps published on success, and monotonic status
  stamps) shared by the lib and component suites, so the two cannot drift into describing
  different servers.
* `src/lib/api.projectRevision.test.ts` — the transport: header captured byte for byte,
  `null` when no `ETag` was published (never an invented token), `If-Match` sent with the
  CSRF header, revision adopted from a successful write and from a refusal, wildcard
  forwarded verbatim, archive/restore sending no `If-Match`.
* `src/components/ProjectContextEditor.revisionFence.test.tsx` — the UI through the real
  transport: prompt appearance, draft retention, both actions, route switching, the stale
  page refused before the first keystroke and on a bare Save, the divergence copy, recovery
  through reload, the two conflict actions cannot run together, and the mounted-draft cases:
  a parent that replaced its copy after a sibling write, a bare Save after that replacement,
  an archive that landed on content the draft never displayed, and — the other side of the
  rule, so it does not over-refuse — a status write over unchanged content leaving the draft
  savable on its new token, plus a capture from the previous project that answers after a
  route switch and must leave the new page clean.
* `src/app/api/[...path]/route.test.ts` — the proxy forwards `If-Match` upstream and returns
  the `ETag` on both the `200` and the `409`.
* `apps/api.Tests/ProjectPatchRevisionConflictTests.cs` —
  `PublishedRevision_IsReadableByAnOriginSeparatedBrowser` pins that an origin-separated read
  gets both the published `ETag` and `Access-Control-Expose-Headers: ETag`;
  `StatusWrite_PublishesTheCommittedRevision_AndThatTokenCarriesAnOpenDraftForward` pins the
  archive/restore stamp on success and its use as the base of a following edit;
  `StatusWrite_AfterAnotherWriterCommitted_LeavesStatusUnconditionalButStampMonotonic` pins
  that the status write stays unconditional while still sitting ahead of the commit it landed
  on, so an expired `ETag` never becomes live again.
* `e2e/projects-revision-conflict-tabs.spec.ts` — two real browser tabs against the API and
  Postgres: stale refusal with the draft intact, keep-mine overwrite only after the click,
  reload re-basing followed by a successful save, revision dropped on navigation, a row that
  moved **before the first keystroke** refused with its draft preserved, and a Save with no
  keystroke at all refused instead of silently reverting the other tab, the archive-before-
  typing sequence on two tabs, and a context draft left behind by a sibling details save on
  one page.
