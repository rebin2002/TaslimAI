# Movie Director Story Assistance

This wave extends the existing Movie Director proposal/action architecture for Story development. Story work uses the existing AI Core completion path and cost calculator; it does **not** add a provider, a fifth quality tier, a duplicate cost system, a retry system, or a duplicate usage ledger.

## User-controlled flow

1. The user requests a Story action.
2. `MovieDirectorContextAssembler.AssembleStoryAsync` builds a bounded snapshot containing the locked Movie Guide, current and approved Story revision snapshots, an optional target screenplay scene, up to 40 Movie scenes, and up to 12 Cast and World references.
3. `DirectorCreativeQualityPlanner` selects **Fast**, **Standard**, **Cinematic**, or **Studio**. Auto Director is only a selection mode; it maps internally to existing AI Core capability tiers (`Fast`, `Smart`, or `Advanced`).
4. `MovieDirectorStoryAiService` sends the bounded Story task through the existing AI Core structured-output path. The response must pass bounded Story validation; unavailable, timed-out, malformed, or invalid AI output returns a safe creative-unavailable/invalid error and creates no proposal or fake creative text. Any configured provider/model fallback remains inside the existing AI Core boundary.
5. Internal usage, when returned by AI Core, is completed through the existing `UsageLedgerService` with `UsageFeature.Movie`; customer charging remains disabled by the existing charging service.
6. The user sees existing content, proposed content, findings, and the proposal status.
7. `POST /api/movie-director/proposals/{proposalId}/approve` is the explicit approval boundary. Rejection cancels the action.
8. `POST /api/movie-director/actions/{actionId}/execute` applies an approved change through `IMovieStoryService`, creating a new editable `Draft` revision with `Authorship = AiSuggested` and a parent revision pointer. Approved revisions are never mutated.

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
| `selectedPassage` | Optional user-selected passage text; it is bounded before entering the AI Core request. |
| `goal` | Optional user goal used for a proposed scene synopsis or proposal title. |

The response adds `storyReview` to the existing `DirectorProposalDto` and `storyContext` to the existing `DirectorProposalResponse`. `storyReview.changes` contains `field`, `target`, `existingContent`, and `proposedContent`; `appliesToStory` distinguishes a revision candidate from diagnostics.

### Story consistency findings

`identify_story_inconsistencies` now returns typed, review-only findings. Each finding has:

| Field | Meaning |
| --- | --- |
| `findingType` | `hard_continuity_conflict`, `possible_inconsistency`, or `creative_suggestion` |
| `severity` | `error`, `warning`, `suggestion`, or `info` |
| `category` | Approved Story, locked Movie Guide, Cast continuity, World continuity, character state, chronology, screenplay fact, or coverage |
| `evidence` | One or more bounded sources with source type/id, revision, and excerpt |
| `affectedTarget` | The Story revision, screenplay scene/element, or project affected |
| `explanation` | Grounded statement of what was observed; no inferred contradiction is presented as fact |
| `suggestedCorrection` | A review suggestion only; it is never applied automatically |
| `confidence` / `uncertainty` | A bounded confidence score and, where applicable, why human review is still needed |

The bounded context includes the current and approved Story revisions, the locked Guide sections, relevant Cast states and lock IDs, and the existing World continuity projection (including its deterministic warnings). Chronology checks use persisted Story and production scene ordering. Lexical checks only report explicit opposing terms (for example, a hard Cast lock for `red` against a scene explicitly containing `blue`); they do not infer unstated plot meaning. Existing World continuity engine warnings are reused as evidence rather than recomputed as a second engine.

No database migration is required: findings are stored in the existing Director action payload/result JSON and remain review-only.

## Task 2 integration notes

- Consume the existing Movie Director endpoints and action status values; do not create a second Story AI endpoint or approval state machine.
- Treat `storyReview` as a diff contract, not as an already-applied revision.
- Only execute an action after `proposal.status === Approved` and `action.status === Ready`.
- Refresh Story after a successful execution and use `currentRevision` for the new editable revision; keep `approvedRevision` as the unchanged approved source of truth.
- Preserve `Authorship` and `ParentRevisionId` when adding future human editing controls. A direct human edit should continue to use the existing Story revision contract with `HumanEdited`.
- The Story proposal planner is a validation/normalization boundary only; it never invents premise, logline, synopsis, treatment, screenplay, dialogue, or passage text. Development/test `MockAiProvider` remains available only through the existing configuration, while production Story work fails honestly when no legitimate AI response is available. Provider and model identifiers remain internal; quality/cost decisions are expressed only through the four Movie quality tiers and existing usage infrastructure.
- Collaboration authorization remains the existing `WorkspaceAccessService` membership check on proposal reads/writes and action execution.
