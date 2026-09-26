# Taslim Movie Director V2 Foundation

## Purpose

Movie Director V2 gives Taslim one provider-neutral Director surface for a Movie Project. It coordinates context, planning, quality recommendations, proposals, user approval, action execution, explainable decisions, and history without creating a second AI/provider system.

Auto Director is a planning mode, not a fifth quality tier. The selectable quality levels remain **Fast**, **Standard**, **Cinematic**, and **Studio**. Auto Director recommends one of those levels per shot using importance, complexity, continuity sensitivity, budget sensitivity, budget limits, and the shared cost-estimation hook.

## Flow

1. `POST /api/movie-director/projects/{movieProjectId}/proposals` resolves a target (`project`, `story_revision`, `scene`, `shot`, `storyboard_version`, `production_version`, or `take`) and assembles a bounded snapshot of the locked Guide, approved Story when available, target hierarchy, relevant Cast/World, cinematography, production, and relevant collaboration state.
2. The quality planner produces a provider-neutral recommendation and rationale. The proposal contains a generic plan, estimated USD amount when the shared estimator can determine one, and a generated shot action in `PendingApproval`.
3. `POST /api/movie-director/proposals/{proposalId}/approve` changes the proposal to `Approved` and its actions to `Ready`. This is the explicit user approval boundary.
4. `POST /api/movie-director/actions/{actionId}/execute` is the separate execution command. An action that is not `Ready` is rejected with `DIRECTOR_APPROVAL_REQUIRED`.
5. The Movie Director action executor calls the existing `IMovieStudioService`, which creates the existing `movie.clip.generate` Generation Job. The job continues through the existing worker, provider resilience, cost guardrails, quality control, asset publication, and usage ledger.
6. Action results and safe history events are persisted for explainability and replay review. Provider/model identifiers, prompts, storage keys, raw provider responses, and secrets are not returned by Director contracts.

Rejecting a proposal cancels its pending actions. Proposal and action status transitions are durable and history events are append-only records for the current foundation.

## Contracts and persistence

The additive migration `AddMovieDirectorFoundation` creates:

| Entity | Purpose |
| --- | --- |
| `DirectorProjectContext` | Versioned, hashed Movie Project context snapshot |
| `DirectorProposal` | Explainable plan awaiting approval |
| `DirectorAction` | Typed execution intent with approval state and payload |
| `DirectorActionResult` | Safe result history for an action |
| `DirectorDecision` | Quality recommendation, rationale, and estimated cost per shot |
| `DirectorHistoryEvent` | Context, proposal, approval, execution, and failure timeline |

Context snapshots are versioned and SHA-256 hashed. Payloads and rationale are bounded by EF column lengths. All reads and writes are workspace-authorized through `WorkspaceAccessService`.

### Targeted context contract

`MovieDirectorContextAssembler` never loads the full Cast/World projection for a target. It resolves every target through the requested Movie Project, then selects:

- target-linked scenes, shots, storyboard/production versions, and takes;
- Cast whose names or approved-screenplay character references occur in the target, plus their target-relevant continuity locks;
- World entities attached through target scene/shot usage, plus target-scoped facts, active locks, and linked references;
- the approved Story revision when one exists, otherwise an explicit `null` Story selection;
- target-scoped reviews, assignments, and comments.

The serialized snapshot has a deterministic `100,000`-byte maximum and a separate optional-material budget. Locked Guide sections, Cast locks, World locks, and continuity facts are critical inputs: they are never silently truncated; an over-budget critical set fails with `DIRECTOR_CONTEXT_BUDGET_EXCEEDED`. Snapshot provenance identifies source kind, entity/revision, lock state, and priority. `AssembledAt` is excluded from identity by using a stable snapshot timestamp, so identical source state produces the same SHA-256 hash. Assembly duration is recorded in the safe Director history event for performance instrumentation.

## Shared Wave 5 boundaries

The Director does **not** add routing, providers, provider enablement, spending, billing, or autonomous execution. It reuses:

- `IMovieStudioService` and the existing Movie generation request contract;
- `IGenerationJobService` and `movie.clip.generate`;
- `IGenerationCostEstimator` via `MovieDirectorCostEstimator`;
- `IGenerationBudgetService` and the existing generation budget guardrails;
- `IProviderResilienceOrchestrator` through the existing job/provider execution path;
- `IGenerationQualityControl` through the existing worker;
- `IGeneratedAssetPublisher`, Assets, private Stored Files, and Usage Ledger.

The default movie provider remains the existing unavailable provider. No new provider is enabled and no paid provider call is made by this foundation.

## API surface

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/movie-director/projects/{movieProjectId}/proposals` | Assemble context and create a proposal |
| `GET` | `/api/movie-director/proposals/{proposalId}` | Read proposal, plan, action state, and context |
| `GET` | `/api/movie-director/projects/{movieProjectId}/history` | Read bounded safe Director history |
| `POST` | `/api/movie-director/proposals/{proposalId}/approve` | Explicitly approve proposed actions |
| `POST` | `/api/movie-director/proposals/{proposalId}/reject` | Reject and cancel proposed actions |
| `POST` | `/api/movie-director/actions/{actionId}/execute` | Execute an already approved action |

All mutating routes use the existing antiforgery middleware. Normal user responses are provider-neutral.

## Follow-up dependencies and risks

- A future UI can render the proposal and approval states without changing the backend contracts.
- A future Auto Director planner can expand from one shot to a bounded multi-shot plan by adding more `DirectorDecision` and `DirectorAction` rows; it should continue to use the same approval boundary.
- A future real movie provider must be enabled only through the existing `IMovieVideoProvider` registration and must retain Generation Job concurrency, resilience, QC, private storage, and usage accounting.
- Budget estimates are conservative hooks, not a spending authorization. Unknown estimates remain unknown and are subject to existing Wave 5 guardrails when the Generation Job executes.
- The migration was generated with EF Core and the model snapshot is updated. Applying it remains part of the normal deployment migration process; this task does not deploy or merge.
