# Project Revision Fence — Client Integration

Status: implemented in `apps/web`. Companion to the server-side fence that shipped in
PR #1256 (merged at `bfe6e81f`), which itself carries the guarantees first specified in
PR #929 and regression-pinned in PR #1174.

## Why this document exists

The API has refused conflicting project edits since `bfe6e81f`: every `Project` row
carries a revision stamp (`UpdatedAt`), `GET /api/projects/{id}` publishes it as an
`ETag`, and `PATCH /api/projects/{id}` declines any write whose `If-Match` names a
revision that no longer holds the row.

That protection only applies to a client that actually declares a revision. Until this
change, `apps/web/src/lib/api.ts` sent no `If-Match` on any project write, so every
browser edit took the server's backward-compatible last-write-wins path: two tabs of the
same member, or a member and a collaborator, silently replaced each other, and the loser's
stale text landed on top of the newer save with nothing shown to anyone. The server was
safe; the client never used it.

## Where the revision comes from

`apps/web/src/lib/projectRevision.ts` — `ProjectRevisionSession`, one per project id.

1. **Capture at the start of editing.** The first keystroke in an edit form calls
   `session.begin()`, which issues `GET /api/projects/{id}` and records the `ETag` from
   that answer. The token therefore describes the copy the draft was written against —
   which is the only thing an `If-Match` precondition can honestly mean.
2. **The token is carried verbatim.** The published format is
   `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` (seven fractional digits, a 100-nanosecond grid).
   JavaScript's `Date` keeps three, so any value rebuilt from `project.updatedAt` would be
   a token the server can never match. The client stores the header string exactly as it
   arrived and never parses it. `api.getProjectWithRevision` returns the header, not a
   derived field.
3. **Advance from the write's own answer.** A successful `PATCH` publishes the revision it
   committed, so the session adopts it without a follow-up read. That avoids both an extra
   round trip and a race in which a third writer slips between the write and the re-read.

## What each edit path does

| Path | Component | Behaviour |
| --- | --- | --- |
| Project context (instructions / notes) | `ProjectContextEditor` | Fenced `PATCH`; conflict prompt |
| Name, description, type | `ProjectForm` (used by the detail view and the project list) | Fenced `PATCH`; conflict prompt, draft retained |
| Archive / restore | `ProjectDetailView`, `ProjectsView` | Unconditional by design; adopts the published revision (see below) |

`ProjectsView` and `ProjectDetailView` own the session so that a status change made by the
same view can advance the token instead of stranding an open draft on a revision the server
has already moved past.

The unfenced `api.updateProject` remains in the module as the legacy transport for any
caller outside these paths; nothing under `apps/web/src` uses it after this change.

## Conflict handling

When the server answers `409 PROJECT_REVISION_CONFLICT`:

* nothing of the draft is written (the API applies no fields on a refusal);
* the unsaved input stays on screen, exactly as typed;
* the member is shown `projects.revisionConflict`, localised in English, Arabic and Kurdish,
  with two explicit actions and no automatic retry:
  * **Load the latest saved version** — `session.reloadLatest()` re-reads the row, replaces
    the draft, and re-bases the session on the newly published revision;
  * **Keep my changes and save over the newer version** — `session.saveOverLatest()` sends
    `If-Match: *`, which is the API's documented "apply over the live row" path. This is the
    only way a newer revision is overwritten, and it requires that click.
* The expired token is never quietly swapped for the live one. A `409` answer carries the
  revision that currently holds the row (`ApiError.revision`, taken from the response's
  `ETag`), but that value is used only for display of, and for the deliberate reload of,
  the newer copy. An unresolved conflict is not an implicit acceptance of either side.
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
* Both endpoints publish the revision they committed, and both move it forward monotonically
  (the atomic, non-decreasing update fixed in #1256). The client therefore adopts the stamp
  from the archive/restore answer, so the member's own status click cannot strand their
  open text draft on a revision the server has already passed.
* The consequence, stated plainly: a concurrent archive from another tab cannot be refused.
  Archive/restore remain last-write-wins by design. If a future requirement makes concurrent
  archiving user-visible (for example an undo affordance), the precondition belongs on those
  endpoints first and the client follows with `archiveProjectWithRevision` already in place.

No behaviour was added to the API in this change; the endpoints, their fences, and their
authorization checks are unchanged.

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
  capture-at-edit-start, two-tab refusal, live revision on the conflict, explicit wildcard
  override, reload re-basing, no re-read per save, no unfenced write, cross-project token
  isolation, archive/restore adoption, verbatim 100-nanosecond precision, legacy call silence.
* `src/lib/projectRevisionFenceFixtures.ts` — the stub of the fenced endpoint (including
  `400 INVALID_IF_MATCH` and monotonic archive stamps) shared by the lib and component
  suites, so the two cannot drift into describing different servers.
* `src/lib/api.projectRevision.test.ts` — the transport: header captured byte for byte,
  `null` when no `ETag` was published (never an invented token), `If-Match` sent with the
  CSRF header, revision adopted from a successful write and from a refusal, wildcard
  forwarded verbatim, archive/restore sending no `If-Match`.
* `src/components/ProjectContextEditor.revisionFence.test.tsx` — the UI through the real
  transport: prompt appearance, draft retention, both actions, route switching.
* `src/app/api/[...path]/route.test.ts` — the proxy forwards `If-Match` upstream and returns
  the `ETag` on both the `200` and the `409`.
* `apps/api.Tests/ProjectPatchRevisionConflictTests.cs` —
  `PublishedRevision_IsReadableByAnOriginSeparatedBrowser` pins that an origin-separated read
  gets both the published `ETag` and `Access-Control-Expose-Headers: ETag`.
* `e2e/projects-revision-conflict-tabs.spec.ts` — two real browser tabs against the API and
  Postgres: stale refusal with the draft intact, keep-mine overwrite only after the click,
  reload re-basing followed by a successful save, and revision dropped on navigation.
