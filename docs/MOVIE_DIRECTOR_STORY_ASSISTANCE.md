# Movie Director Story Assistance

This wave extends the existing Movie Director proposal/action architecture for provider-independent Story development. It does **not** add a provider, AI router, cost system, retry system, or usage ledger.

## User-controlled flow

1. The user requests a Story action.
2. `MovieDirectorContextAssembler.AssembleStoryAsync` builds a bounded snapshot containing the locked Movie Guide, current and approved Story revision snapshots, an optional target screenplay scene, up to 40 Movie scenes, and up to 12 Cast and World references.
3. `DirectorStoryProposalPlanner` creates a deterministic proposal and review delta. No paid provider is called.
4. The user sees existing content, proposed content, findings, and the proposal status.
5. `POST /api/movie-director/proposals/{proposalId}/approve` is the explicit approval boundary. Rejection cancels the action.
6. `POST /api/movie-director/actions/{actionId}/execute` applies an approved change through `IMovieStoryService`, creating a new editable `Draft` revision with `Authorship = AiSuggested` and a parent revision pointer. Approved revisions are never mutated.

A proposal stores the base revision ID. Execution fails safely with `DIRECTOR_STORY_BASE_CHANGED` if the Story changed after proposal creation.

## Story action contract

The existing route is reused:

```http
POST /api/movie-director/projects/{movieProjectId}/proposals
```

Relevant request fields:

| Field | Purpose |
| --- | --- |
| `storyAction` | One of `develop_premise`, `improve_logline`, `expand_synopsis`, `create_refine_treatment`, `propose_screenplay_scene`, `rewrite_selected_passage`, `improve_dialogue`, `tighten_pacing`, or `identify_story_inconsistencies`. |
| `targetSceneId` | Optional screenplay scene ID from the bounded Story revision context. |
| `targetElementId` | Optional screenplay element ID for selected-passage rewrites. |
| `selectedPassage` | Optional user-selected passage text; it is not required for the deterministic test-safe planner. |
| `goal` | Optional user goal used for a proposed scene synopsis or proposal title. |

The response adds `storyReview` to the existing `DirectorProposalDto` and `storyContext` to the existing `DirectorProposalResponse`. `storyReview.changes` contains `field`, `target`, `existingContent`, and `proposedContent`; `findings` is used by the review-only inconsistency action; `appliesToStory` distinguishes a revision candidate from diagnostics.

## Task 2 integration notes

- Consume the existing Movie Director endpoints and action status values; do not create a second Story AI endpoint or approval state machine.
- Treat `storyReview` as a diff contract, not as an already-applied revision.
- Only execute an action after `proposal.status === Approved` and `action.status === Ready`.
- Refresh Story after a successful execution and use `currentRevision` for the new editable revision; keep `approvedRevision` as the unchanged approved source of truth.
- Preserve `Authorship` and `ParentRevisionId` when adding future human editing controls. A direct human edit should continue to use the existing Story revision contract with `HumanEdited`.
- The current planner is deterministic and mock-safe. A future provider-backed planner can replace only the proposal-generation implementation while retaining bounded context, proposal review, approval, stale-base protection, and the existing Director action executor boundary.
- Collaboration authorization remains the existing `WorkspaceAccessService` membership check on proposal reads/writes and action execution.
