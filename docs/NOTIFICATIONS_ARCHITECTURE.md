# Notifications Center Architecture

## Decision

Taslim now has two related but distinct surfaces. **Activity** remains the complete history and live projection of generation jobs. **Notifications** are durable, user-scoped attention records created only for events that benefit from follow-up: terminal generation success, terminal generation failure, recovered long-running claims, and supported billing payment failures. A generation is not a notification merely because it was queued or progressed.

## Durable model

`Notification` is stored in `Notifications` with `UserId`, `WorkspaceId`, optional project, generation job, and asset references, a stable `Type`, a safe resource title, `CreatedAt`, and nullable `ReadAt`. The unique `(UserId, DeduplicationKey)` index makes event writes idempotent. Read state is per user, so workspace members do not share read/unread state accidentally.

The notification payload contains only safe references and a server-generated same-origin destination. It does not expose provider, model, cost, internal error, storage key, or delivery-provider data. The model is future-ready for other delivery channels, but this wave has no email, push, SMS, or external delivery worker.

## Event policy and integration

The generation worker writes one completion or failure notification after the authoritative terminal transition. Expired running claims are re-queued and produce one `generation.attention` notification so a user can see that a long-running job needs attention. Notification persistence is isolated behind a safe wrapper and cannot turn a successful or failed generation into a different job outcome. Supported payment-failure transitions notify workspace owners and admins using the existing payment lifecycle; billing remains authoritative if the in-app record cannot be written.

Activity does not duplicate these records. It continues to project every job, including queued, running, cancelled, completed, and failed work with its existing per-user `ActivityReadState`.

## API and authorization

All notification endpoints require authentication and validate workspace membership through `WorkspaceAccessService`. List and unread-count operations filter by both current user and workspace. Mark-one and mark-all operations require the workspace in the request body and are idempotent. The full center is `/notifications`; the existing history is available at `/activity`.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` | `/api/notifications?workspaceId=...` | Paged notification records and unread count. |
| `GET` | `/api/notifications/unread-count?workspaceId=...` | Header badge count. |
| `POST` | `/api/notifications/{id}/read` | Mark one current-user notification read. |
| `POST` | `/api/notifications/read-all` | Mark all current-user notifications in the workspace read. |

## UI and localization

The header bell uses the unread count and opens a compact panel with a mark-one control, mark-all control, and relevant destination link. The full center is responsive and links to Asset Library, Activity, or Billing. English, Arabic, and Kurdish Sorani strings are provided through `LocaleProvider`; the existing document-level direction switch and additive RTL rules handle Arabic and Sorani layouts.

## Migration and integration hotspots

`20260924120000_AddNotifications` is additive and creates the table, foreign keys, list indexes, and unique deduplication index. Apply it after the existing Activity and generation migrations. The shared files most likely to conflict with parallel waves are `TaslimDbContext.cs`, `Program.cs`, `GenerationJobExecution.cs`, `PaymentServices.cs`, `AppShell.tsx`, `ActivityBell.tsx`, `api.ts`, `i18n.ts`, `navigation.ts`, and `globals.css`. Keep the notification service separate from `ActivityCenterService`; only the shell unread badge is shared.

## Test coverage

The web suite covers the new route build and the updated notification navigation contract. API integration coverage adds notification read/read-all idempotency, unread counts, workspace authorization, event deduplication, successful generation notification creation, failed generation notification creation, and relevant destinations.
