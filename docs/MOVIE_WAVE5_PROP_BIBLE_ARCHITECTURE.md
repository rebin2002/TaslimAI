# Movie Wave 5 Prop Bible

Wave 5 adds a canonical recurring-prop **Prop Bible / Production Sheet** without changing the existing Movie World model.

## Canonical records

`MovieProp` remains the backward-compatible prop identity and is the source for existing World and production-reference packages. A `MoviePropBible` is an additive one-to-one production sheet for that prop and contains:

- stable identity key and production role;
- visual identity and continuity rules;
- reusable visual reference Asset links with role, notes, and provenance;
- named variants with state, visual notes, and optional reference Asset;
- scene/shot usage projection from the existing `MovieWorldUsage` records;
- immutable version snapshots with content hash and provenance;
- explicit approval and lock metadata.

Version snapshots capture the bounded references, variants, and usages at creation time. Older versions remain available after a new draft is created. A locked version cannot be silently edited; a later revision must be created explicitly and the locked snapshot remains authoritative for the prior production decision.

## API

All routes are authenticated, workspace/collaboration authorized, and mutating routes use the existing antiforgery middleware.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/movie-studio/projects/{id}/prop-bible` | List bounded Prop Bible sheets and deterministic detection results |
| `GET` | `/api/movie-studio/props/{propId}/production-sheet` | Read one production sheet |
| `PUT` | `/api/movie-studio/props/{propId}/production-sheet` | Create/update the current editable sheet and append a version |
| `POST` | `/api/movie-studio/props/{propId}/production-sheet/references` | Link a workspace Asset as a visual reference |
| `POST` | `/api/movie-studio/props/{propId}/production-sheet/variants` | Add a named prop variant |
| `POST` | `/api/movie-studio/props/{propId}/production-sheet/versions` | Append an explicit version |
| `POST` | `/api/movie-studio/prop-bible/versions/{versionId}/review` | Approve or reject a pending version |
| `POST` | `/api/movie-studio/prop-bible/versions/{versionId}/lock` | Lock an approved version |
| `GET` | `/api/movie-studio/projects/{id}/prop-bible/recurring-detection` | Run the read-only recurring-prop detector |

`/prop-bible` aliases are also available on the prop-level routes for clients that use that terminology.

## Deterministic detection seam

`IMovieRecurringPropDetector` is registered as a singleton and implemented by `DeterministicMovieRecurringPropDetector`. It receives existing prop identities, existing scene/shot usage links, and bounded planned-shot text. It marks a prop recurring only when the deterministic evidence shows repeated scene/shot presence. It does not call an AI provider, create a Generation Job, create Assets, alter accounting, or mutate Movie records.

The detector response explicitly carries `providerFree: true` and a detector version so future implementations can be compared without changing the API shape.

## Migration

`AddMovieWave5PropBible` is additive. It creates `MoviePropBibles`, `MoviePropBibleReferences`, `MoviePropBibleVariants`, and `MoviePropBibleVersions`, with foreign keys to existing Movie props, projects, Assets, and users. Existing Movie props and World usages remain valid without a Prop Bible row; read endpoints return a compatibility projection from the existing prop record until a sheet is created.

## User-facing safety

The normal-user contract contains no provider key, model identifier, credential, raw execution handle, or prompt. Prop Bible operations are planning and review operations only. No external generation or paid provider is enabled by this wave.
