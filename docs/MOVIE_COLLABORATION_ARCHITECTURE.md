# Movie Studio Collaboration Foundation

## Scope

Full Movie Projects now have a project-scoped collaboration boundary on top of the existing workspace boundary. The foundation persists human team membership, production roles, permission overrides, object-level comments and mentions, review decisions, production assignments, and optional production credits.

Taslim AI responsibilities remain outside this membership model. The schema has no AI user, bot user, or synthetic `ApplicationUser` path. Generation continues through the existing server-owned Generation Job/provider boundary.

## Roles and permissions

Supported production roles:

- Producer / Project Owner
- Director
- Writer
- Art Director
- Cinematographer
- Editor
- Sound & Music

Supported permissions:

- `View`
- `Comment`
- `Edit`
- `Generate`
- `Approve`
- `ManageBudget`
- `ManageTeam`
- `FinalApproval`

Role defaults are server-side policy. A team member can receive explicit allow/deny overrides, persisted in `MovieTeamMemberPermissions`. The project owner is the human account that created the Movie Project and is always treated as having all movie permissions; the owner cannot be removed or reassigned.

A reviewer is only the user assigned to a `MovieReview`. Being assigned as a reviewer does **not** modify team membership or permissions. A reviewer must already be a human movie-team member and must have `Approve` or `FinalApproval` for the decision type. Generation access is independent; no review route grants `Generate` or any usage/credit authority.

## Object-level authorization

Every collaboration object is scoped by `MovieProjectId` and, where applicable, a typed `TargetType` + `TargetId` pair. The service verifies that the target belongs to the same Movie Project before writing comments, review requests, or assignments.

The authorization sequence is:

1. Authenticated, active user.
2. Workspace membership for the Movie Project workspace.
3. Movie team membership for the Movie Project.
4. Effective movie permission after role defaults and explicit overrides.
5. Target ownership by the same Movie Project.

Unauthorized movie collaboration reads intentionally return the same not-found envelope as a missing Movie Project. Mutations return `403` for permission failures and bounded `400` errors for invalid role, permission, target, mention, review, or assignment data.

Existing Movie Studio guide/planning writes require `Edit`. Scene/shot generation requires `Generate`, preserving the existing expensive-AI rate limit and Generation Job usage path.

## Persistence

`AddMovieStudioCollaboration` adds:

- `MovieTeamMembers`
- `MovieTeamMemberPermissions`
- `MovieComments`
- `MovieCommentMentions`
- `MovieReviews`
- `MovieProductionAssignments`
- `MovieProductionCredits`

The migration backfills one Producer/project-owner membership for each existing Movie Project from its existing `CreatedByUserId`, so previously created movies do not become inaccessible. Credits require a current movie-team member and store only a human `UserId` plus optional display credit name.

## API contracts

All endpoints require authentication; state-changing endpoints require the existing CSRF header.

- `GET /api/movie-studio/projects/{movieProjectId}/collaboration`
- `POST/PATCH/DELETE /api/movie-studio/projects/{movieProjectId}/collaboration/team[/{memberId}]`
- `POST /api/movie-studio/projects/{movieProjectId}/collaboration/comments`
- `POST /api/movie-studio/projects/{movieProjectId}/collaboration/comments/{commentId}/resolve`
- `POST /api/movie-studio/projects/{movieProjectId}/collaboration/reviews`
- `POST /api/movie-studio/projects/{movieProjectId}/collaboration/reviews/{reviewId}/decision`
- `POST/PATCH /api/movie-studio/projects/{movieProjectId}/collaboration/assignments[/{assignmentId}]`
- `POST/DELETE /api/movie-studio/projects/{movieProjectId}/collaboration/credits[/{creditId}]`

Comments persist normalized mention joins. The API accepts explicit mentioned user IDs and rejects mentions that do not target current human movie-team members; it does not create users from display names or `@handles`.

## Integration notes

- The current web Movie Studio UI can continue to use the existing project endpoints; collaboration is an additive API surface for the next UI stage.
- Future invitation flows should add workspace/project membership through the existing Identity and workspace membership services; do not create an AI account to represent Taslim AI.
- Budget/credit accounting remains outside this foundation. `ManageBudget` is a permission capability only; it does not charge, deduct, reserve, or authorize Generation Job usage by itself.
- Final approval is a review decision state and permission check, not an automatic publish or deployment action.
- Future notifications can consume comment mentions, review requests/decisions, and assignment changes without weakening authorization.
