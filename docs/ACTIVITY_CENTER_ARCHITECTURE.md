# Activity Center Architecture

## Decision

Taslim’s Activity Center is a **safe projection over the existing `GenerationJob` architecture**. It does not create a second job table, duplicate queue, or notification record for every generation. The existing generation job remains the source of truth for lifecycle, progress, timestamps, cancellation, outputs, and workspace authorization. The Activity Center adds only one small persistence concern: per-user read state.

The protected `/notifications` route is the user-facing activity surface. It lists queued, running, completed, failed, and cancelled work across the current personal workspace. Users can leave Image, Document, Presentation, or Research Studio and return later to see the current status or open the resulting Asset. The projection also leaves room for future Social, Voice, Music, and Movie job types without changing the activity storage model.

## Domain model

`GenerationJob` already owns the following fields needed by this feature:

- lifecycle status and cancellation state;
- progress percentage;
- user-facing title when supplied by a Studio;
- creation, queue, start, completion, failure, and cancellation timestamps;
- generated outputs; and
- workspace, project, and creator relationships.

The additive `ActivityReadState` entity contains `UserId`, `GenerationJobId`, and `ReadAt`. A unique index on `(UserId, GenerationJobId)` makes the operation idempotent. A read state is scoped to the user who is viewing the activity. The underlying job remains workspace-scoped, so a member can see the same workspace activity while maintaining an independent unread state.

Completed Assets are resolved through the existing `Asset.SourceGenerationJobId` relationship. No asset metadata, storage key, provider, model, or cost fields are included in the activity response.

## Safe activity projection

The API maps internal job values to a stable user-facing contract:

| Internal job state or type | Activity value |
| --- | --- |
| `Pending` or `Queued` | `Queued` |
| `Running` | `Running` |
| `Succeeded` | `Completed` |
| `Failed` | `Failed` |
| `Cancelled` | `Cancelled` |
| `image.generate` | `image` |
| `document.generate` | `document` |
| `presentation.generate` | `presentation` |
| `research.generate` | `research` |
| future supported types | their safe category, or `other` |

An empty job title receives a safe category title such as “Document generation” or “Research”. Failed and cancelled activities use fixed safe messages. The projection never returns provider names, model identifiers, provider cost, internal error text, storage providers, or storage keys.

The completion timestamp is the first available value from `CompletedAt`, `FailedAt`, or `CancelledAt`. The progress value is clamped to the public range `0–100`.

## API surface

All endpoints require authentication. Every workspace operation first verifies membership through `WorkspaceAccessService`.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` | `/api/activity?workspaceId=...&page=...&pageSize=...&status=...` | Return a paged safe activity projection, including unread count. |
| `GET` | `/api/activity/unread-count?workspaceId=...` | Return the current user’s unread count for the workspace. |
| `POST` | `/api/activity/{jobId}/read` | Idempotently mark one workspace job as read. Requires the workspace ID in the request body. |
| `POST` | `/api/activity/read-all` | Mark all currently unread jobs in the authorized workspace as read. |

The status filter accepts `All`, `Queued`, `Running`, `Completed`, `Failed`, and `Cancelled`. The `Completed` filter translates to the existing `Succeeded` generation status. Invalid or unsupported values produce an empty filtered result rather than exposing internal status details.

## Read behavior

A job is unread when no `ActivityReadState` exists for the current user and job. New jobs therefore appear in the header badge immediately, including work that is still queued or running. Opening a completed Asset marks the activity read. The explicit “Mark read” action supports queued, running, failed, and cancelled work. “Mark all read” is idempotent and only inserts missing state rows.

The header bell requests only the unread count for the authenticated personal workspace. It refreshes while the shell is open and renders a compact badge instead of the previous unconditional notification dot. The Activity Center refreshes active work on a short client-side interval while the page is open; it does not create a new server-side polling worker or scheduled process.

## User experience and accessibility

The Activity Center uses a compact card layout that is readable on desktop and collapses to a single-column layout on small screens. Status filters remain horizontally scrollable on mobile. Each card exposes the safe title, localized job type, status, progress when work is active, created time, terminal time when available, safe failure text, and an Asset link when a generated Asset exists.

English, Arabic, and Kurdish Sorani strings are provided. The existing `LocaleProvider` continues to set `lang` and `dir`, while additive RTL rules reverse card controls, badges, and action order. The page uses semantic tabs, live activity updates, readable button labels, and visible focus-compatible controls without exposing job identifiers in the UI.

## Migration and rollout

`20260923154035_AddActivityReadStates` creates `ActivityReadStates`, its foreign-key relationships to users and generation jobs, and the unique per-user/per-job index. The migration is additive and can be applied after the current generation-job and asset migrations. Existing jobs appear automatically in the Activity Center as unread the first time a user opens it; no backfill is required.

The implementation does not modify the generation worker, any Studio handler, Asset storage, or Social Media Studio. Existing generation completion behavior remains authoritative.

## Testing

The targeted API tests cover safe type and status projection, unread count, idempotent read behavior, and workspace isolation. The frontend tests cover status/type normalization, active-state detection, safe failure display, and refresh merging without duplicate jobs. Existing generation, asset, and Studio tests remain unchanged.

## Integration notes

The branch intentionally touches the following shared files because they are integration points:

| Shared file | Expected conflict if another wave edits the same area | Resolution |
| --- | --- | --- |
| `apps/api/Domain/Entities.cs` | Entity additions near `GenerationJobOutput` or other domain entities | Keep `ActivityReadState`; reconcile adjacent insert location. |
| `apps/api/Persistence/TaslimDbContext.cs` | DbSet or `OnModelCreating` additions | Keep the read-state DbSet, unique index, and cascade relationships. |
| `apps/api/Program.cs` | Service registrations and `using` directives | Keep `IActivityCenterService` registration; preserve all other wave registrations. |
| `apps/web/src/lib/api.ts` | Shared frontend type and API method additions | Keep `ActivityItem`, `ActivityList`, and the four activity methods. |
| `apps/web/src/components/AppShell.tsx` | Header notification/bell markup | Keep `ActivityBell` in the existing notification slot. |
| `apps/web/src/lib/i18n.ts` | Large three-locale translation object | Preserve all `activity.*` keys in each locale. |
| `apps/web/src/app/globals.css` | Shared stylesheet tail or topbar styles | Keep the additive activity block and merge duplicate notification badge rules if necessary. |

No conflict is expected with `apps/web/src/components/SocialMediaStudioView.tsx` because this branch does not modify that implementation. The main API and frontend additions are isolated in `ActivityController`, `ActivityCenterService`, `ActivityContracts`, `ActivityCenterView`, `ActivityBell`, and activity state tests.

## References

[1]: GENERATION_JOBS_ARCHITECTURE.md "Taslim generation job architecture"
[2]: ASSET_ARCHITECTURE.md "Taslim asset architecture"
[3]: AUTHENTICATION.md "Taslim authentication architecture"
