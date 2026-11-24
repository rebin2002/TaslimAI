# Academic Workflow Engine — Phase C foundation

Own lane for Epic #1259 Phase C: the bounded, resumable academic project engine
(plan → discover → screen → extract → synthesize → chapter draft → verify) with human
approval checkpoints, idempotency, safe budget ceilings, a durable state contract, and
an integrity firewall that makes fabricated quotations, page numbers, identifiers, and
empirical findings structurally impossible to commit.

Everything here is **pure**: no EF/DbContext, no HTTP, no provider SDKs, no Program.cs
wiring. State moves through `IAcademicWorkflowStateStore` and provider work through
`IAcademicStageHandler`, so this lane is file-disjoint from the shared research lanes
until integration.

## Files

| File | Role |
| --- | --- |
| `AcademicWorkflowContracts.cs` | Stages, requests, plans, source records (access levels), evidence, themes, chapters, verification reports, outputs, budget policy. |
| `AcademicWorkflowState.cs` | Durable snapshot, stage/approval records, budget ledger (reserve/preflight/meter), canonical JSON + stage input fingerprints. |
| `AcademicIntegrity.cs` | Identifier validators (DOI/ISBN/arXiv/PMID/URL), access-level quotation rules, whole-snapshot integrity guard, citation verifier. |
| `AcademicWorkflowPorts.cs` | State-store port, stage-handler port, run/approval result contracts, engine options. |
| `AcademicWorkflowOrchestrator.cs` | The resumable engine: create, resume slices, checkpoint commits, approval gates, budget pauses, revision invalidation. |

## Key invariants (each has deterministic tests)

1. **Resumable, checkpointed stages.** Every stage completion is committed as a
   monotonic revision; a resume from the last checkpoint never re-runs a stage whose
   committed idempotency key still matches its recomputed input fingerprint. A crash
   mid-attempt leaves an already-charged reservation so billing stays honest.
2. **Idempotency at three levels.** Creation by `(workspaceId, requestKey)` →
   deterministic project id; stage replay by `projectId|stage|fingerprint`; checkpoint
   commits by deterministic checkpoint key (`DuplicateCheckpoint` is a safe no-op);
   approval decisions keyed to the exact checkpoint revision.
3. **Human approval gates.** Configurable per request: Plan, SourceLibrary (after
   screening), Synthesis, ChapterDraft, FinalVerification. The engine parks in
   `AwaitingApproval` and cannot advance without a decision for the current checkpoint.
   `RequestRevision` re-runs the gated stage with reviewer feedback (changing its
   fingerprint) and clears every downstream artifact; `Rejected` cancels terminally.
4. **Safe budget limits.** Preflight refuses an attempt whose estimate (with a safety
   factor) crosses per-stage or total ceilings, in USD and provider calls; measured
   usage is metered exactly once per attempt key; overrun past the total ceiling pauses
   the project without merging the stage output. No accidental recharges or duplicates.
5. **Citations require source access.** `MetadataOnly` sources can appear as
   bibliographic references but can never carry quotations, paraphrase content, or
   locators; `Abstract` access allows quotation only of words present in the stored
   abstract text, never with a locator; locators require `FullText`. Invalid DOIs/ISBNs
   (checksum-level), unresolvable citations, citations of excluded/retracted sources,
   off-outline drafts, and incomplete screening are blocking integrity findings — the
   merge is discarded and never reaches durable state.

## Integration handoff checklist (other lanes / follow-up PRs)

- **DI (`Program.cs`)**: register `AcademicWorkflowOrchestrator` as singleton-scoped
  service with the store adapter, the stage handler set, and `TimeProvider.System`.
  Nothing in this lane requires framework DI annotations yet.
- **Persistence lane**: implement `IAcademicWorkflowStateStore` over a new table
  (`academic_workflow_projects`): `project_id` PK, `workspace_id`, `request_key`
  (unique with workspace), `revision` bigint, `state` int, `snapshot_json` jsonb, plus
  a `academic_workflow_checkpoints` table with unique `(project_id, checkpoint_key)`
  to give `TryCommitAsync` its duplicate-checkpoint semantics. The commit must be one
  transaction: insert checkpoint row (unique-violation → `DuplicateCheckpoint`),
  update snapshot `WHERE revision = @expected` (0 rows → `RevisionConflict`).
  EF migration ownership belongs to the persistence lane; the snapshot JSON shape in
  `AcademicWorkflowSerialization` is the stable storage contract.
- **Job pipeline (`ResearchGenerationJobHandler` / Generation lane)**: add an
  `academic-*` job type whose handler deserializes an `AcademicWorkflowRequest`, calls
  `StartAsync`/`ResumeAsync` per job attempt, maps `AcademicWorkflowRunResult.Status`
  onto job progress (`AwaitingApproval`/`PausedBudget` end the worker attempt without
  failure; `Completed` proceeds to export; `StageFailure` relies on the existing retry
  policy), and leaves budget/integrity codes in job diagnostics. One resume call can
  span multiple stages, so job attempts should call `ResumeAsync` in a bounded loop.
- **Provider lanes**: implement `IAcademicStageHandler` per stage over the shared
  `ResearchProviders` abstractions: Discover wraps OpenAlex/Crossref/Open Library/
  Google Books/repository catalogs and must return `AcademicSourceRecord`s with real
  access levels (`AccessNote` records the license basis); Extract/Synthesize/Draft
  run through AI Core with the evidence context assembled from snapshot artifacts.
  Handlers return typed `AcademicStageOutput` + measured `AcademicUsageRecord` only —
  they must not write state, and the engine will reject outputs that touch another
  stage's artifact.
- **UX/API lanes**: surface `AcademicPendingApproval.ReviewerPrompts`, the source
  library (with `AccessLevel` and retraction flags), the evidence matrix (themes +
  contradicting evidence), and chapter drafts with per-block citations; POST decisions
  via `SubmitApprovalDecisionAsync`. Export (DOCX/PDF) must call
  `AcademicIntegrityGuard.Validate` on the final snapshot before rendering and refuse
  on blocking findings; reference ordering can reuse the existing
  `ResearchExportReferenceOrdering`.
- **Tenancy**: every entry point takes `WorkspaceId`; the store adapter must scope
  load/commit by workspace, and uploads/user-provided sources (Phase B lane) enter as
  private sources with their own access levels — never as quotable full text unless
  the uploaded file genuinely contains the text.

## What this lane deliberately does NOT do

- No production wiring, endpoints, migrations, keys, or billing changes.
- No provider calls and no model output yet — the engine is exercised only through
  scripted handler doubles (`apps/api.Tests/AcademicWorkflow*`).
- No DOCX/PDF rendering (reuses the document lane at integration time).
- No semantic "is this claim true" checking — the guard enforces *provenance and
  access*, which is what makes fabrication detectable; Phase B/D lanes cover bias and
  contradiction quality policy on top of the evidence matrix this lane persists.
