# Movie Guide V2 Foundation

## Purpose

Movie Guide V2 is the canonical production memory for a Movie Studio project. It is a structured, versioned source of truth for Director actions; it is not a free-form prompt field. The foundation is additive to the existing Movie Studio foundation and does not create character or world entities.

## Schema

`MovieContinuityGuide` remains the one-to-one guide root for `MovieProject` and now stores:

- `CurrentRevisionNumber`
- `LockedRevisionNumber`
- `LockedAt`
- `LockedByUserId`

`MovieGuideRevision` is an append-only content record with a unique `(MovieContinuityGuideId, RevisionNumber)` and these seven JSON sections:

| Section | Shape | Ownership boundary |
| --- | --- | --- |
| `story_bible` | JSON object | Movie Guide-owned story facts and decisions |
| `character_bible_references` | JSON array | References only; character entities remain a later task |
| `world_bible_references` | JSON array | References only; world entities remain a later task |
| `visual_bible` | JSON object | Visual language, palette, texture, design rules |
| `cinematography_bible` | JSON object | Lens, framing, movement, camera grammar |
| `audio_bible` | JSON object | Dialogue, music, ambience, narration rules |
| `continuity_bible` | JSON object | Props, screen direction, temporal and state constraints |

The server validates JSON syntax, section shape, and a 50,000-character per-section bound. The database stores canonical JSON text and never stores an unstructured prompt surrogate.

The legacy `MovieContinuityGuide` text columns remain for compatibility with the existing Movie Studio API and provider snapshots. New guide revisions are canonical; the legacy PATCH route translates its fields into a new structured revision and is blocked while locked.

## Revisions and locking

- A new Movie project creates revision `1` in `Draft` state.
- Creating a revision appends revision `N + 1`; existing revisions are not edited.
- Locking records the selected current revision, timestamp, and user on both the revision and guide root.
- While a guide is locked, revision creation and legacy guide updates return `409 MOVIE_GUIDE_LOCKED`.
- Unlocking clears the guide lock and returns all revisions to editable draft state; history remains intact.
- Locking an older revision is rejected. This avoids silently selecting stale facts as authoritative.
- `GET /director-context` returns only the locked revision, with `isAuthoritative: true`. An unlocked guide returns `409 MOVIE_GUIDE_NOT_LOCKED`.

This makes the lock an explicit hand-off boundary: Director actions must read the authoritative context and cannot mutate it through the read model.

## API contracts

All routes require authentication and workspace membership. Mutations require the existing CSRF header.

| Method | Route | Contract |
| --- | --- | --- |
| `POST` | `/api/movie-studio/projects/{id}/guide/revisions` | Append a validated `MovieGuideRevisionRequest`; returns `MovieGuideRevisionResponse` |
| `GET` | `/api/movie-studio/projects/{id}/guide/history` | Return all revisions, newest first, plus current/locked numbers |
| `POST` | `/api/movie-studio/projects/{id}/guide/lock` | Lock the current revision, optionally by `revisionNumber` |
| `POST` | `/api/movie-studio/projects/{id}/guide/unlock` | Clear the lock while retaining history |
| `GET` | `/api/movie-studio/projects/{id}/director-context` | Return the locked `MovieDirectorContextDto` only |
| `PATCH` | `/api/movie-studio/projects/{id}/guide` | Backward-compatible legacy update that appends structured revision data; blocked while locked |

The Director read model contains the Movie Project ID, guide ID, authoritative flag, locked revision number, lock timestamp, and the seven typed section descriptors. Character and world section values are opaque references for Tasks 3/4 to resolve later; this task does not own or duplicate those entities.

## Authorization

Every service method first loads the guide through its Movie Project and checks `WorkspaceAccessService.IsMemberAsync`. Missing or cross-workspace project/history/context reads return a safe `404`; no guide content is disclosed. The service never treats a browser-supplied workspace ID as proof of access.

## Migration

`20260926101052_AddMovieGuideRevisions` adds the lock/revision root columns and creates `MovieGuideRevisions` with restrictive user foreign keys, unique revision numbering, and history/status indexes. It also backfills revision `1` for existing Movie Studio guides from the legacy fields so existing projects have a valid structured source of truth after migration.

## Tests

`apps/api.Tests/MovieGuideTests.cs` covers:

- structured section persistence and seven-section read shape;
- revision numbering and history;
- lock and authoritative Director context reads;
- mutation rejection while locked;
- unlock and subsequent revision creation;
- cross-workspace history and Director-context isolation.

The test fixture uses SQLite `EnsureCreated` for relational API coverage; the EF migration is generated for PostgreSQL production use.

## Integration notes

- Movie Director should depend on `IMovieGuideService.GetDirectorContextAsync` or an equivalent server-side adapter, never on legacy text fields or browser-provided guide JSON.
- Director actions must treat `isAuthoritative` as a required precondition and must not write back through the context DTO.
- Tasks 3/4 can later resolve the `character_bible_references` and `world_bible_references` IDs against their own entities without changing this schema.
- Provider routing and AI Core remain outside this foundation. Existing video/provider code can continue using compatibility snapshots until a later integration explicitly consumes the locked structured sections.
