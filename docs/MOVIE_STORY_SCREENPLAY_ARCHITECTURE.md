# Movie Studio Story and Screenplay Foundation

This foundation adds a story workspace to the existing Movie Studio without creating a second movie hierarchy. A `MovieStory` is a one-to-one extension of the existing `MovieProject`; screenplay scenes link to existing `MovieScene` rows when available and carry non-owning `ActNumber`, `SequenceNumber`, and `SceneIdentifier` values for screenplay structure.

## Domain model

| Entity | Responsibility |
| --- | --- |
| `MovieStory` | Current premise, logline, synopsis, treatment, approval pointers, and workspace authorization context. |
| `MovieStoryRevision` | Immutable-after-transition snapshot of story text, authorship, parent revision, change summary, and approval history. |
| `MovieScreenplayScene` | Ordered screenplay scene within a revision, with slugline, act/sequence identifiers, stable scene identifier, and optional existing `MovieSceneId`. |
| `MovieScreenplayElement` | Ordered typed content: `Action`, `Dialogue`, `Parenthetical`, `Transition`, or `Note`. Dialogue requires `CharacterName`. |

A revision stores the full story snapshot and full structured screenplay snapshot. No approved text is overwritten by later human or AI-assisted drafts. `Authorship` distinguishes `Human`, `AiSuggested`, and `HumanEdited`; no provider/model call is made by this task.

## Revision lifecycle

- New revision: `Draft` and becomes the story's `CurrentRevisionId`.
- Submit: `Draft -> Submitted`; story approval state becomes `InReview`.
- Approve: `Submitted` or `Draft -> Approved`; `ApprovedAt`, `ApprovedByUserId`, and story `ApprovedRevisionId` are recorded.
- Reject: `Draft` or `Submitted -> Rejected` with a required reason.
- A revision in `Approved`, `Rejected`, or `Superseded` is immutable. A new revision can use it as `ParentRevisionId`.

The approved pointer is deliberately separate from the current pointer, so a later draft can be edited while the last approved screenplay remains addressable and unchanged.

## API

All endpoints are authenticated and workspace-membership protected. State-changing endpoints use the existing CSRF middleware.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/movie-studio/projects/{movieProjectId}/story` | Read the story, current/approved revision snapshots, and revision summaries. |
| `GET` | `/api/movie-studio/projects/{movieProjectId}/story/revisions` | List revision summaries in descending revision order. |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/story/revisions` | Create a complete story/screenplay revision. |
| `GET` | `/api/movie-studio/projects/{movieProjectId}/story/revisions/{revisionId}` | Read one structured revision. |
| `POST` | `.../{revisionId}/submit` | Submit a draft for review. |
| `POST` | `.../{revisionId}/approve` | Approve a reviewable revision. |
| `POST` | `.../{revisionId}/reject` | Reject with a required human-readable reason. |

The create contract includes `premise`, `logline`, `synopsis`, `treatment`, `authorship`, optional `parentRevisionId`, optional `changeSummary`, and ordered `scenes`. Each scene includes `sceneIdentifier`, optional `actNumber`, optional `sequenceNumber`, optional `movieSceneId`, `slugline`, optional synopsis, and ordered typed `elements`.

## Persistence and integration

Migration `AddMovieStoryScreenplayFoundation` adds four tables with bounded text columns, revision/scene/element ordering indexes, a unique story per MovieProject, and foreign keys back to `MovieProjects` and optional `MovieScenes`. It reuses the existing workspace authorization service and does not add Movie, Act, Sequence, Scene, or Shot ownership tables. The existing Movie Studio controller/service remains the integration surface; story behavior is isolated behind `IMovieStoryService`.

Future AI-assisted writing can create `AiSuggested` or `HumanEdited` revisions through the same contract. An AI adapter should never mutate a submitted/approved revision; it should create a child revision and preserve the parent pointer and change summary for review.
