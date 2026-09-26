# Taslim.ai Movie Studio V2 Architecture

**Status:** Foundation integration on `integration/movie-v2-foundation`
**Scope:** Movie Studio V2 domain, API, persistence, workspace shell, collaboration, and Director foundation
**Authoritative document:** This document supersedes the individual foundation notes when they describe overlapping ownership. The focused documents remain useful for endpoint-level detail.

## 1. Product boundary

Movie Studio has two customer-facing workflows:

- **Quick Movie:** a short brief creates a durable Movie Project and an internal plan. It does not expose the Full Movie approval workspace and does not queue expensive rendering merely because the project was created.
- **Full Movie Project:** a persistent production workspace with story, cast, world, scenes, storyboard, production, collaboration, Director, and future delivery surfaces. Foundation modules honestly show unavailable/upcoming states rather than fabricating output.

The browser owns presentation and navigation. The API owns authentication, workspace/project authorization, persistence, planning, generation-job orchestration, provider safety, assets, QC, and usage accounting.

## 2. Canonical production hierarchy

There is one production hierarchy:

```text
MovieProject
  └── MovieAct
        └── MovieSequence
              └── MovieScene
                    └── MovieShot
                          ├── MovieProductionVersion (planned/candidate artifact funnel)
                          └── MovieTake (canonical rendered take/version)
```

- `MovieProject` is the workspace-owned production root.
- `MovieAct`, `MovieSequence`, `MovieScene`, and `MovieShot` are the canonical planning hierarchy. Story/screenplay records do not create a competing hierarchy; screenplay scenes may reference a canonical `MovieScene`.
- `MovieProductionVersion` represents a production artifact moving through `ShotPlan`, `StoryboardCandidate`, `ApprovedStoryboard`, `ProductionKeyframe`, `ApprovedKeyframe`, `MotionPreview`, `ProductionRender`, and `SelectedFinalTake`.
- `MovieTake` is the durable rendered take attached to a shot. It is not a second name for the production-version approval funnel.
- `MovieTakeApproval` governs take selection/domain state. Production-version review governs the funnel artifact. `MovieReview` remains a collaborative review/request workflow.

Quality levels are exactly **Fast**, **Standard**, **Cinematic**, and **Studio**. **Auto Director** is a planning mode that recommends one of those levels; it is not a fifth quality level.

## 3. Movie Guide and continuity authority

The locked Movie Guide is the authoritative production hand-off. Its sections are:

1. Story Bible
2. Character Bible references
3. World Bible references
4. Visual Bible
5. Cinematography Bible
6. Audio Bible
7. Continuity Bible

`MovieCharacter` and the World entities (`MovieLocation`, `MovieSet`, `MovieSetVariation`, `MovieProp`, references, facts, usages, and locks) remain reusable structured source systems. The Guide stores references and project-level production guidance; it does not take ownership of complete character/world records.

- Character continuity locks are character/state scoped.
- World facts and locks are project/entity scoped.
- Guide locks are guide/revision scoped.
- Guide writes require the project collaboration `Edit` permission and are rejected while the authoritative revision is locked.
- Director actions are proposal/action records and cannot silently rewrite Guide facts.

## 4. Story and screenplay

`MovieStory` is a one-to-one extension of `MovieProject`. `MovieStoryRevision` carries authorship (`Human`, `AiSuggested`, or `HumanEdited`) and approval state. When an approved revision exists, Director and production decisions must use that approved revision rather than an unfinished draft. Screenplay scenes can map to canonical `MovieScene` rows and never own the production hierarchy.

## 5. Cast, World, and bounded projections

Characters, locations, sets, variations, props, and references are durable reusable records. Mutations validate:

- authenticated active user;
- workspace membership;
- Movie Project ownership;
- same-project target references;
- Asset ownership in the same workspace;
- collaboration permission for the operation.

Generation and Director context use projected fields and stable IDs, not unbounded copies of the entire project. Continuity snapshots are bounded structured projections. The API keeps provider payloads, secrets, storage keys, and raw provider responses server-side.

## 6. Shot Designer and cinematography

Cinematography belongs to the canonical `MovieShot` and is also referenced by the Guide's Cinematography Bible. Structured intent covers shot size, focal length, lens, depth/aperture, angle, movement, frame rate, lighting, palette/look, and composition.

Provider capability classification remains explicit:

- Native
- Translated
- Simulated/Post
- Unsupported

A preset is not evidence of provider-native support. The Shot Designer validates and normalizes intent before it is persisted, and production requests retain the intent as structured metadata.

## 7. Collaboration and approvals

Collaboration is project-scoped and uses these permissions:

`View`, `Comment`, `Edit`, `Generate`, `Approve`, `ManageBudget`, `ManageTeam`, and `FinalApproval`.

Team membership and permission overrides are distinct from workspace membership. Review assignments never grant Generate, ManageBudget, or spending authority implicitly.

Approval responsibilities remain separate:

| Record | Responsibility |
|---|---|
| `MovieTakeApproval` | Canonical take/domain approval |
| `MovieProductionVersion` review/status | Production funnel candidate approval or rejection |
| `MovieReview` | Collaborative review/request workflow |
| Story revision approval | Authoritative screenplay revision |
| Director proposal approval | Permission to execute a proposed Director action |
| Collaboration assignments | Work coordination, not authority escalation |

Comments, mentions, reviews, assignments, credits, and team permissions are all checked against the same Movie Project and workspace boundary.

## 8. Movie Director pipeline

The Director is contextual assistance, not a permanent generic chatbot and not a new provider system:

```text
authoritative Guide + approved Story revision + relevant Cast/World state/locks
+ relevant Scene/Shot + cinematography + production/collaboration state
    → bounded, versioned Director context snapshot
    → proposal + quality/cost recommendation
    → explicit user approval
    → approved action execution
    → existing Movie Studio Generation Job path
```

`DirectorProjectContext` stores a versioned, hashed snapshot. `DirectorProposal` explains a plan. `DirectorAction` is not executable until explicitly approved. `DirectorDecision` records quality rationale and cost estimates. `DirectorActionResult` and `DirectorHistoryEvent` retain safe explainability data.

Execution reuses the existing `IMovieStudioService`, `GenerationJob` worker, provider resilience, cost guardrails, QC, Asset publication, and Usage Ledger. No Director action enables providers or bypasses approval, budget, or idempotency boundaries.

## 9. Full Movie workspace

The premium shell is routed at `/create/movie/{projectId}/{module}` with foundation modules for:

`Overview`, `Story`, `Cast`, `World`, `Scenes`, `Storyboard`, `Production`, `Edit`, `Audio`, `QC`, `Exports`, and `Team`.

The shell reads durable API records, keeps the Director/Inspector contextual, shows continuity and production signals, and honestly labels unfinished modules. It does not manufacture thumbnails, footage, approvals, or access controls. Quick Movie remains on the simple brief/result surface.

## 10. Wave 5 infrastructure

Movie Studio V2 consumes the existing infrastructure. It does not create a second:

- provider router;
- cost engine;
- retry/resilience engine;
- Generation Job system;
- QC system;
- Asset publication system;
- Usage Ledger.

Movie generation remains provider-neutral and unavailable by default unless an explicitly configured, verified provider is enabled outside this foundation task. No paid provider calls or customer charging activation occur here.

## 11. Authorization and security

Mutation and read boundaries follow:

```text
authenticated active user
  → workspace access
    → Movie Project/team access
      → collaboration permission
        → same-project target ownership
```

Mutation routes retain antiforgery/CSRF protection. Cross-workspace and cross-project references are rejected or returned as not found according to the API contract. Reviewer access cannot silently become Generate or budget authority. Provider credentials and raw provider payloads never reach browser contracts.

## 12. Persistence and migration policy

All ten foundations changed overlapping EF model metadata. The integrated branch preserves the approved feature migrations and adds explicit additive reconciliation migrations after the model was composed:

- `20260926131600_ReconcileMovieV2Integration`
- `20260926131800_ReconcileMovieV2CollaborationDirector`

The reconciliation migrations are generated from the final `TaslimDbContext` model rather than hand-editing the generated snapshot. Their `Up` methods contain additive table/column/index operations; the destructive operations are limited to reversible `Down` methods. EF reports no pending model changes after the final migration is compiled.

A real PostgreSQL database was not available in the sandbox during validation, so migration application and idempotent script execution against PostgreSQL remain deployment-time checks.

## 13. Validation expectations

The integration gate covers API build/tests, focused Movie V2 behavior, collaboration/authorization boundaries, Director approval/execution behavior, EF pending-model validation, frontend tests/build, Quick Movie regression, conflict-marker checks, and `git diff --check`. Browser Playwright execution and live PostgreSQL application must be reported separately when their services are unavailable.
