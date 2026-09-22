# Unified Asset Architecture

## Purpose

Batch 3.7 introduces **Asset** as Taslim’s user-facing, reusable content object. An Asset may represent an image, document, presentation, video, audio recording, music output, research deliverable, social content, generic file, or future content type. The foundation is provider-independent and does not implement any studio or external generation provider.

An Asset belongs to one workspace, may be assigned to one project, and may reference a privately stored file. Assets can be listed, searched, renamed, reassigned, archived, restored, previewed when the media type is safely supported, and downloaded through authenticated API access.

## Separation of Responsibilities

| Object | Responsibility | User-facing library object |
|---|---|---:|
| `StoredFile` | Private storage metadata, storage key, MIME type, byte size, processing status, and provider adapter linkage | No |
| `GenerationJobOutput` | Immutable provenance link between a Generation Job and an output, optionally through `StoredFileId` | No |
| `Asset` | Reusable product object with name, description, type, lifecycle, project assignment, and optional file/provenance links | Yes |

This separation avoids turning storage rows into product records and avoids overloading Generation Job outputs with mutable library metadata. Storage provider names, storage keys, provider/model details, and internal metadata are not returned by Asset API contracts.

## Persistent Model

`Asset` uses a GUID primary key and stores `WorkspaceId`, optional `ProjectId`, `CreatedByUserId`, optional `StoredFileId`, optional `SourceGenerationJobId`, `Name`, optional `Description`, generic `AssetType`, optional `MimeType`, `Status`, optional bounded `MetadataJson`, and UTC creation/update/archive timestamps.

The initial generic types are `image`, `document`, `presentation`, `video`, `audio`, `music`, `research`, `social`, `file`, and `other`. The initial lifecycle is `Active` or `Archived`. Adding a new handler does not require adding a provider-specific column.

The schema uses restrictive foreign keys for workspace, creator, stored-file, and Generation Job provenance. A deleted project sets `ProjectId` to null so the Asset remains reusable at workspace level. Indexes support workspace/status recency, workspace/type/status, project/status recency, workspace/name, file linkage, and source-job provenance.

## Generated Output Publication

Generation handlers return provider-independent output descriptors. A descriptor can include an output type, an existing `StoredFileId`, a bounded generated file artifact, safe output metadata, and an optional Asset publication descriptor.

`IGeneratedAssetPublisher` owns the reusable publication path:

1. Validate the generic Asset type before writing data.
2. Persist a generated file through `FileProcessingService` and the configured private `IFileStorageService` adapter.
3. Prepare a `GenerationJobOutput` linked to that file.
4. Prepare an `Asset` that inherits the job workspace, creator, optional project, and source-job provenance.
5. Let the worker atomically persist the output, Asset, and successful job transition.
6. Remove generated storage if cancellation wins the completion race or publication fails before commit.

The deterministic `system.test` handler now creates `generation-result.json`, stores it privately, links it through `GenerationJobOutput`, and publishes a `file` Asset. It remains provider-free, zero-cost, deterministic, asynchronous, and cancellation-cooperative. Failed or cancelled jobs publish no Asset.

Future Image, Document, Presentation, Movie, Voice, Music, Research, or Social handlers should return the same generic output descriptors. They must not implement their own Asset tables, expose provider details, or bypass the publication service.

## Authorization and Private Downloads

All Asset routes require authenticated Identity cookies. `AssetService` owns workspace authorization and project/workspace validation by reusing `WorkspaceAccessService`. Direct get, update, archive, restore, download, and cross-workspace project assignment all enforce the Asset workspace boundary.

Storage remains private. The API does not expose a public bucket URL or storage key. `GET /api/assets/{id}/download` authorizes the caller, opens the private `StoredFile` through `IFileStorageService`, and streams the response. `inline=true` is accepted only for image MIME types and supports authenticated image previews. Mutating routes retain CSRF validation.

## API Surface

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/assets` | Paginated workspace list with project, type, status, and text filters |
| `GET` | `/api/assets/{id}` | Retrieve one authorized Asset |
| `PATCH` | `/api/assets/{id}` | Rename, edit description, or assign/remove a project |
| `POST` | `/api/assets/{id}/archive` | Archive without deleting the stored file |
| `POST` | `/api/assets/{id}/restore` | Restore to the active library |
| `GET` | `/api/assets/{id}/download` | Stream the authorized private file |

The list API defaults to active Assets, clamps pagination, validates generic types, and safely searches names and descriptions. PostgreSQL uses case-insensitive `ILIKE`; SQLite tests use an equivalent lowercase comparison.

## Product Surface

`/assets` is a protected product page in the primary navigation. It provides a responsive card grid, search, project/type/status filters, pagination, empty/loading/error states, file download, image preview support, metadata editing, project assignment/removal, and archive/restore actions. Project details show a compact Asset section linking into the same unified library.

Visible strings are localized in English, Arabic, and Kurdish Sorani, and the layout uses the existing RTL direction system. The UI contracts intentionally omit storage provider names, storage keys, provider/model details, and source-job identifiers.

## Migration and Operations

The additive EF Core migration `AddAssets` creates only the `Assets` table, relationships, and indexes. Existing `DataProtectionKeys`, `StoredFiles`, Generation Job tables, usage data, chat data, and project/workspace data remain intact. Production continues to use the advisory-lock migration runner before normal startup.

Deploy the API migration before relying on Asset publication. The existing R2/S3-compatible configuration is reused in production; no new queue, object store, bucket, public URL, or secret is required.

## Deletion Boundary

Batch 3.7 intentionally provides archive/restore, not hard deletion. Archiving an Asset does not delete its `StoredFile`. Future retention or permanent-deletion work must define provenance, shared-file references, storage deletion, audit, and recovery policy before it is introduced.
