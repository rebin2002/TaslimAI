# Academic Research — Integration Slice (issue #1267, Phase D-in-progress)

Owner scope: **persistence and API orchestration** that connects the merged
AcademicEvidence foundation (#1264, Phase B) and the AcademicWorkflow engine
(#1263, Phase C) to authenticated, workspace-scoped academic projects.

This slice ships the rails: persisted research-plan intake, source screening and
evidence matrices carried into the Phase B tables, approval gates, idempotent and
resumable checkpointing, budget ceilings, and citation-verification boundaries
across the seam. It deliberately does **not** connect discovery or extraction to
any literature provider, and it does not touch the AcademicSources search
endpoint or the AcademicTemplates/export integration, which are owned by other
workers.

Everything below is what the code in this repository actually does today. The
"Honest status" table at the end lists what is *not* implemented; nothing in this
slice claims a stage is functional until a provider lane registers a handler for
it — the API says so itself (HTTP 501 with a machine-readable stage list).

## Architecture

```
apps/api/AcademicIntegration/
├── AcademicIntegrationServiceCollectionExtensions.cs  composition root (AddAcademicProjects)
├── AcademicProjectsController.cs                      authenticated workspace-scoped API
├── AcademicProjectsService.cs                         intake, fencing, run/approve/budget, projection
├── AcademicEvidenceProjector.cs                       Phase C snapshot → Phase B evidence lane bridge
└── Persistence/
    ├── AcademicIntegrationEntities.cs                 project row + append-only checkpoint ledger
    ├── AcademicIntegrationModelBuilderExtensions.cs   ApplyAcademicIntegrationModel (indexes, FKs)
    └── EfAcademicWorkflowStateStore.cs                IAcademicWorkflowStateStore over TaslimDbContext
```

Composition (`Program.cs`): a single `builder.Services.AddAcademicProjects(builder.Configuration)`
call pulls in `AddAcademicEvidence()` / `AddAcademicEvidencePersistence()` (previously
defined-but-unwired), binds the `AcademicWorkflow` configuration section to the engine
options, registers the EF state store, the projector, the service, and a per-scope
`AcademicWorkflowOrchestrator` over whatever `IAcademicStageHandler`s the container
holds — which in production is *none* (see "Seams").

Persistence (`TaslimDbContext` + migration
`20261124065935_AddAcademicResearchIntegration`): nine new tables, all additive —
the seven `AcademicEvidence*` tables of Phase B (first time any composition root
referenced them), plus `AcademicWorkflowProjects` (one row per project; canonical
snapshot JSON; `Revision` watermark for compare-and-swap; `EvidenceProjectedRevision`
projection watermark) and `AcademicWorkflowCheckpoints` (append-only ledger, unique
`CheckpointKey`, one row per committed revision). The Postgres migration carries the
partial unique index (`"DuplicateOfSourceId" IS NULL`) exactly like the SQL Server
baseline. `dotnet ef migrations remove --revert` parity was checked: `Down` drops all
nine tables and nothing else; the migration does not touch any other worker's schema.

## What the API offers

All routes require authentication and workspace membership. Unknown-or-foreign project
ids answer the identical 404; non-member access to a *known* project answers 403.
Every mutation is antiforgery-validated like the rest of the API surface, and every
mutation route also carries the platform's existing abuse-boundary policies via
`[EnableRateLimiting]`: intake and checkpoint decisions ride the generation policy,
`run` — the route that spends stage work once provider lanes connect — rides the
expensive-AI policy, and a spend-ceiling raise rides account security. Reads stay on
the global limiter only (asserted by `AcademicProjectsApiTests`).

| Route | Behavior |
| --- | --- |
| `GET  /api/workspaces/{workspaceId}/academic-projects` | Paginated project list (`X-Page`, `X-Page-Size`, `X-Total-Count`). |
| `POST /api/workspaces/{workspaceId}/academic-projects` | Persist research-plan **intake** only — validated by `AcademicWorkflowRequest.Validate`, stored as revision 0 with an `intake` checkpoint. Never auto-runs. `Idempotency-Key` header (preferred) or body `requestKey`; replays return the existing project with HTTP 200 and write nothing. |
| `GET  /api/academic-projects/{projectId}` | Snapshot summary: stage states, pending approval, approval history, budget usage, artifact counts, `connectedStages` / `missingStages`, `evidenceCurrent`. |
| `POST /api/academic-projects/{projectId}/run` | Resumable step: advances the workflow as far as it can through committed checkpoints until a gate or the end. If the next actionable stage has no registered handler, answers **501 `ACADEMIC_STAGE_NOT_CONNECTED`** with `missingStage`, `connectedStages` and `resumableAtRevision`, and the stored state does not move. |
| `POST /api/academic-projects/{projectId}/approvals/{kind}` | Approval decisions (`Approved`, `RequestRevision`, `Rejected`); replays are no-ops. Returns `APPLIED` / `ALREADY_APPLIED`, or 409 `NO_PENDING_APPROVAL`. RequestRevision clears the gated stage's artifact and everything downstream, carries reviewer notes into the re-run fingerprint, and leaves earlier artifacts (e.g. the discovered library when re-screening) intact. |
| `POST /api/academic-projects/{projectId}/budget` | Raise ceilings (never lower). Unpauses a `PausedBudget` project only when the ceilings actually change; an equal-after-clamping request answers `BUDGET_UNCHANGED` without a checkpoint. |
| `GET  /api/academic-projects/{projectId}/evidence-matrix` | Reads the Phase B `AcademicMatrixCell`/`Finding` rows scoped to `(workspaceId, projectId)`. |
| `GET  /api/academic-projects/{projectId}/verification` | `AcademicIntegrityGuard.Validate` recomputed live from the committed checkpoint, plus the stored verification report and the boundary policy text. |
| `GET  /api/academic-projects/{projectId}/checkpoints` | The project's own checkpoint ledger (revision-dense, append-only). |

Privacy and vocabulary: response DTOs never expose provider names, model names,
endpoints, billing rails or routing decisions. `AcademicProjectTextPolicy.Sanitize`
scrubs the stored free-text surfaces that can echo reviewer notes or stage errors
(`provider`→`service`, etc., 600-char caps), and a test scans every route's body for
forbidden tokens. Usage is reported only as the project's own USD/call ledger
ceilings and spend, which the intake budget the workspace itself supplied.

## Idempotency, fencing and resumability

- **Intake** is idempotent per `(workspaceId, requestKey)`: the create path probes for
  an existing row before touching the change tracker, and concurrent first-writers
  race into the unique index, where exactly one wins (`DbUpdateException` → loser
  replays the winner's row via lookup). One project row and one `intake` checkpoint
  per key, always.
- **Commits** are compare-and-swap on `Revision` inside a transaction:
  `UPDATE … WHERE ProjectId = @p AND Revision = @expected`, plus a checkpoint-key
  uniqueness probe before insert. Outcomes are `Committed`, `RevisionConflict`, or
  `DuplicateCheckpoint` — a replayed checkpoint key never rewrites state. Every
  committed revision also gets its ledger row in the same transaction, so reloading
  a project is always consistent with its checkpoint history.
- **Workspace fence**: `TryCommitAsync` re-reads the stored row's `WorkspaceId` and
  refuses any snapshot that reaches across it (as a revision conflict, so a forged
  commit cannot probe which half of the pair is wrong). A test forges a snapshot by
  round-tripping canonical JSON with the fence swapped, and the store rejects it.
- **Resumability** is the engine's contract, made durable here: every stage attempt
  that commits lands in the ledger; a `run` after a crash replays from the last
  committed checkpoint with no re-execution of completed stages and no double
  billing (billable attempts are reserved-then-metered per `attemptKey`; duplicates
  resolve to `DuplicateCheckpoint`).
- **Approval and budget actions** are themselves checkpointed (`approval|…`,
  `budget-raise|…` keys), so replayed decisions and replayed raises are no-ops by
  construction, not by convention.
- **Combined tree (committed snapshot + derived Phase B tables + projection
  watermark)**: evidence projections run one-at-a-time per project behind an
  in-process gate (`AcademicProjectionGate`), and inside the gate the durable
  store — not the caller's copy — decides what is projected. A projection call
  whose snapshot revision no longer matches the committed tip is refused outright
  (no derived rewrite, no watermark claim), a resume that lost the checkpoint race
  never projects its rejected copy, and the watermark advances only while the
  committed tip still matches exactly what was just written. The monotonic
  `EvidenceProjectedRevision` can therefore never certify rows produced by a stale
  writer; a tree left honestly stale self-heals on the next run, as before.
  Behavior pinned by
  `Stale_projection_calls_are_refused_until_the_current_revision_is_projected`.

## Citation-verification boundaries across the seam

The projector (`AcademicEvidenceProjector`) turns committed Phase C snapshots into
Phase B rows under fixed conventions:

- Protocol id = `"wf-" + projectId` (namespace `academic-workflow`); source and
  evidence ids are deterministic SHA-256-derived GUIDs of the project id plus the
  stage's own ids — reruns re-key onto the same rows, never forks.
- Bibliographic data arrives as a **provider assertion origin**, and retraction
  notices carried by a stage are recorded with `Origin = ProviderAssertion`: the
  evidence lane treats them as a lead, not a verdict. A stage-asserted retraction
  excludes the source through the screening merge, but the stored provenance
  verdict stays `Unverified` — the lane will not certify a withdrawal it did not
  read from a registry itself.
- **Access levels bind claims.** Metadata-only sources yield
  `BibliographicOnly` support and never a verifiable quote (`NotSupportedAtAccessLevel`).
  Abstract-level verbatim quotes are containment-checked against the stored
  abstract text and earn `VerifiedAgainstSource` only when the words are actually
  there (`WF_ABSTRACT_CONTAINMENT` otherwise marks them unsupported). Full-text
  paraphrases are stored but flagged: no retrieved copy has been handed across the
  seam yet (`WF_FULLTEXT_BODY_ABSENT`), so nothing about them is marked verified.
- **No fabricated page pinpoints.** A locator arriving with no retrieved-copy anchor
  is recorded as rejected using the lane's own vocabulary
  (`LOCATOR_MEMBER_ASSERTED`) plus the workflow attribution
  (`WF_LOCATOR_UNANCHORED`), never stored as a citable pinpoint, and no matrix cell
  reports `hasVerifiableLocator` for it. Tests assert zero stored rows can render a
  pinpoint the lanes cannot support.
- Matrix questions: plan objectives become `q1..qn`, one `rq` row for the research
  question itself, plus slugged evidence topics; cells/findings come from
  `EvidenceMatrixBuilder` — the projector never writes a cell directly.
- Screening merge: the workflow's included/excluded decisions override the pipeline's
  inclusion for this project, but a pipeline integrity exclusion (e.g. duplicate of
  source) is never silently re-included; reasons from both lanes are concatenated.

## Shared seams (coordination points for the other workers)

1. **`IAcademicWorkflowStateStore` (implemented)** — the Phase C engine port now has a
   durable implementation. Any lane that runs the orchestrator over this store gets
   the checkpoint/fence/idempotency guarantees above for free.
2. **`AddAcademicStageHandler<THandler>()` (open)** — provider lanes (#1266-style
   discovery/extraction handlers) register `IAcademicStageHandler` services through
   this extension on the `AcademicProjectsBuilder`; the per-scope orchestrator picks
   them up automatically, `connectedStages`/`missingStages` update, and the 501s
   retire per stage as handlers arrive. No changes to this slice's files are needed
   to connect a stage; the contract is exercised end-to-end by
   `AcademicProjectsConnectedFlowTests`, which wires the Phase C scripted handlers
   through exactly this seam.
3. **Full-text body handoff (deferred seam)** — the projector currently stores
   `FullText = null` because no lane has handed retrieved bodies to Phase B yet.
   When the AcademicSources worker lands a retrieved-copy store, the seam is one
   field: pass the copy's artifact id as the body origin and the containment/anchor
   checks that already run for abstracts extend to pages. Locator rejection stays
   honest in the meantime.
4. **Export/templates (untouched)** — citations rendered from `AcademicEvidence*`
   rows are keyed by the same deterministic ids, so the AcademicTemplates worker can
   re-render a project's bibliography without knowing anything about the workflow
   snapshot. Nothing in this slice reads or writes template state.
5. **AcademicSources search endpoint (untouched)** — no route, DTO, or table in this
   slice overlaps it. Discovery results reach the workflow only via a future stage
   handler, through `AcademicWorkflowSnapshot.Sources`.

## Honest status

| Capability | Status | Evidence |
| --- | --- | --- |
| Authenticated workspace-scoped project API | **Implemented** | `AcademicProjectsApiTests` (401/403/404 fencing, CSRF, pagination) |
| Persisted research-plan intake, idempotent | **Implemented** | request-key replay tests; single row + single checkpoint |
| Idempotent, resumable, append-only checkpoints | **Implemented** | `AcademicWorkflowStoreTests` (CAS, replay, ledger density, fence) |
| Approval gates incl. RequestRevision/Reject lineage | **Implemented** | connected-flow approval + revision tests |
| Budget ceilings, pause, raise, no-double-charge | **Implemented** | budget-gate tests; reserve-then-meter attempt keys |
| Evidence matrix + screening + provenance persisted into Phase B | **Implemented** (for artifacts lanes produce) | projector + matrix API tests; connected drain tests |
| Citation-verification boundaries (access levels, no fabricated pinpoints) | **Implemented** | access-boundary tests |
| Discovery of sources from real providers | **Not connected** — no `IAcademicStageHandler` is registered in production; `/run` answers 501 with the exact missing stage | by design; `missingStages` in every response |
| Extraction over retrieved bodies (full-text) | **Not connected** — body handoff seam deferred (see above) | `WF_FULLTEXT_BODY_ABSENT` flags stored honestly |
| Chapter drafting / real verification runs | **Not connected** — same handler seam; integrity guard runs on committed artifacts only | verification endpoint is recomputation, not execution |
| Background execution of long runs (queue/worker) | **Not implemented** — `/run` advances through committed checkpoints within one request budget | stated in API docs |
| UI surfaces | Out of scope for this slice | — |
| Export re-render on evidence change | Not implemented (template worker owns it) | seam documented above |

No endpoint pretends otherwise: until a provider lane registers handlers, the only
stages that "run" are the ones with handlers, and the API says which are missing.

## Test evidence

- `apps/api.Tests/AcademicWorkflowStoreTests.cs` — persistence contract: intake
  idempotency, CAS conflicts, checkpoint-key replay without rewrite, byte-identical
  snapshot round-trip, both directions of the workspace fence, forward-only
  watermark, ledger ordering and scoping.
- `apps/api.Tests/AcademicProjectsApiTests.cs` — the production composition (no
  handlers): every route's auth envelope, honest 501 with no state movement and no
  spend, replay semantics, validation ceilings, cross-tenant isolation, antiforgery,
  privacy token scan, pagination headers.
- `apps/api.Tests/AcademicProjectsConnectedFlowTests.cs` — same API with all seven
  Phase C scripted handlers registered through the production seam: full gated
  drain to `Completed` with exact per-stage billing, ledger monotonicity, approval
  replay, RequestRevision lineage (earlier artifacts survive), budget pause →
  raise → resume without double charge, projection staleness self-heal and rebuild
  idempotency, access-boundary rows.
- Full API suite (`dotnet test apps/api.Tests`) is green with this branch's changes;
  CI runs it additionally against Postgres.

## Deferred / follow-ups (tracked, not silently skipped)

1. Provider stage handlers (discovery, extraction) — separate lane; register via
   `AddAcademicStageHandler`.
2. Full-text body handoff to Phase B (one-field seam above).
3. Queue-backed multi-request runs with resumable checkpoints already in place.
4. Billing-rail integration review before any paid stage handler ships (this slice
   only enforces ceilings the workspace itself sets; nothing charges).
5. Migration of provider-lane locator vocabularies into the shared
   `SourceLocatorPolicy` codes as more lanes land assertions.
