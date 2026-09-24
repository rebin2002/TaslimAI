# Global Search Architecture

## Scope

Wave 3.3 adds one protected, workspace-aware search surface for projects, conversations, assets, uploaded files, and generation activity. It deliberately uses bounded relational partial matching rather than semantic or vector search. The first version favors predictable authorization, simple operational behavior, and a fast path that can be replaced or extended later without changing the client contract.

## API

`GET /api/search?q={query}&limit={perType}` requires the authenticated Taslim session cookie. `q` is trimmed and bounded to 100 characters. `limit` is clamped to 1–12 results per result type, with a default of 8. Empty queries return an empty grouped response without querying content tables.

The response contains the normalized query, total returned count, and only non-empty groups. Each result has a stable type, opaque resource ID, safe title, optional description, project context, status, non-sensitive metadata, and timestamps. Provider names, model names, raw generation payloads, storage keys, storage providers, signed URLs, and private file contents are never returned. Raw prompts and extracted file text are used only as server-side matching fields.

Results are grouped by `projects`, `conversations`, `assets`, `files`, and `generation`. The API applies a per-type `Take` before materializing results, so a large workspace cannot cause an unbounded response.

## Authorization

The endpoint derives accessible workspace IDs from the authenticated user's `WorkspaceMembers` rows. Every query is constrained by those IDs. Conversations additionally require `Conversation.UserId == authenticatedUserId`, matching the existing chat history boundary. Files follow the existing file policy: project-scoped files are visible to workspace members, while unscoped and conversation-scoped files require the authenticated uploader. Deleted files are excluded. Assets and generation activity follow the established workspace membership policy.

No workspace ID is accepted from the browser. This prevents a caller from widening the search scope by changing a query parameter and keeps membership evaluation server-owned. A user with no memberships receives no results. Cross-workspace tests cover both an unrelated user and a second member of a shared workspace who must not receive another user's private conversation.

## Matching and navigation

Matching is case-normalized partial text matching over the fields that are useful for identification:

| Result type | Search fields | Safe metadata shown |
| --- | --- | --- |
| Projects | Name, description | Type, lifecycle status, updated date |
| Conversations | Title, user/assistant message content | Project name, lifecycle status, last activity |
| Assets | Name, description | Asset type, project name, lifecycle status |
| Uploaded files | Original file name, extracted text | Extension, project name, processing status |
| Generation activity | Title, job type, project name, input JSON | Humanized activity type, job status, project name |

The web route is `/search`. Its result links preserve the existing destinations: projects open their project route, conversations open chat, project-scoped files open the project, conversation files open chat, assets open the Asset Library with a narrow search/status filter, and generation activity opens its generated asset, project, or Activity Center when no more specific destination exists.

The search control is available in the authenticated global top bar on desktop and mobile. The page supports keyboard focus with `Cmd/Ctrl+K`, a compact mobile form, grouped cards, an initial empty state, a no-results state, loading/error states, and EN/AR/KU copy. Locale direction is inherited from the existing provider, including RTL layout adjustments.

## Migration and indexing

No migration is included. The first version uses existing workspace/time/status indexes and bounded per-type queries. The partial-match predicates intentionally use normalized `Contains` expressions so they work in the existing PostgreSQL production provider and SQLite integration test provider. Leading-wildcard substring search will not fully use ordinary B-tree name indexes, but the query is bounded, scoped by indexed workspace predicates, and capped before response materialization.

If search volume or latency warrants a later optimization, PostgreSQL trigram indexes or a dedicated denormalized search projection should be measured against production query plans first. That work should preserve the same authorization predicates and should not be introduced merely for the initial feature.

## Conflict hotspots

The highest-overlap files are `AppShell.tsx`, `globals.css`, `i18n.ts`, and `api.ts`, because they are shared navigation, style, localization, and client-contract surfaces. The backend addition is isolated to a new controller and contract file, while `TaslimDbContext` and existing migrations remain unchanged. The Asset Library page/component has a small compatible change so asset result links can preserve the selected search and lifecycle filter.
