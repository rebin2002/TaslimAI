# Movie World V2 Foundation

## Purpose

Movie World is the durable, provider-neutral world layer for Movie Guide, Storyboard, Shot Designer, and Movie Director. It prevents locations, environments, props, and visual references from being recreated as unrelated generation prompts.

All records are owned by a `MovieProject`, and all scene/shot usage records point back to a reusable world entity by stable ID.

## Persistence model

| Record | Purpose |
| --- | --- |
| `MovieLocation` | Reusable physical or narrative place with visual continuity notes and optional Asset reference |
| `MovieSet` | Reusable set/environment attached optionally to a location; captures environment type, time, weather, and visual description |
| `MovieSetVariation` | Durable state/variation for a set, such as day/night, weather, or lighting; exactly one or zero default states are supported by service behavior |
| `MovieProp` | Reusable prop identity with category, continuity notes, and optional Asset reference |
| `MovieWorldReference` | Project-level moodboard, location, set, prop, continuity, or other visual reference with optional reusable Asset |
| `MovieWorldUsage` | Scene/shot attachment to a location, set, or prop. Duplicate attachments are idempotent at the service boundary |
| `MovieContinuityFact` | Structured fact scoped to project, scene, or shot |
| `MovieContinuityLock` | Active or released lock for a field/value, with `soft` or `hard` strength and audit author |

Existing `MovieLocation.ReferenceAssetId`, character references, and the generic `Asset` library remain intact. No provider URL or provider-specific generation state is introduced by Movie World.

## API contracts

All routes require authentication, workspace membership, and the existing anti-forgery header for mutations. The project read response now includes a `world` object containing `locations`, `sets` (including `variations`), `props`, `references`, `usages`, `facts`, and `locks`.

| Method | Route | Contract purpose |
| --- | --- | --- |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/locations` | Create reusable location |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/sets` | Create reusable set/environment |
| `POST` | `/api/movie-studio/sets/{setId}/variations` | Add a reusable set state/variation |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/props` | Create reusable prop |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/world-references` | Register visual reference and optional Asset link |
| `POST` | `/api/movie-studio/scenes/{sceneId}/world-usage` | Attach a location, set, or prop to a scene or shot |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/continuity-facts` | Persist a project/scene/shot fact |
| `POST` | `/api/movie-studio/projects/{movieProjectId}/continuity-locks` | Persist a soft/hard continuity lock |
| `GET` | `/api/movie-studio/projects/{movieProjectId}` | Read the project and complete world graph |

Errors use the existing envelope and feature-specific codes such as `MOVIE_WORLD_USAGE_INVALID`, `MOVIE_SET_VARIATION_INVALID`, and `MOVIE_CONTINUITY_LOCK_INVALID`.

## Workspace/movie authorization

The service resolves the owning `MovieProject` before every world mutation and checks `WorkspaceAccessService.IsMemberAsync` against the project workspace. Reference Asset IDs are independently verified against the same workspace. Entity attachment validates that the location, set, prop, scene, and optional shot all belong to the same Movie Project/scene. Cross-workspace reads return the existing movie-project-not-found behavior and do not disclose ownership.

## Generation and integration seams

When Movie Studio queues a scene or shot clip, it stores a provider-neutral `WorldContextJson` in `MovieGenerationInput`. The snapshot is built from stable world IDs and projected fields only:

- project-relevant locations, sets and their variations, props, references;
- scene/shot world usages;
- applicable continuity facts;
- active continuity locks.

`MovieVideoGenerationRequest` carries this snapshot as an optional field. The existing `IMovieVideoProvider` boundary remains unchanged in behavior and no provider-specific generation logic was added. A provider implementation may consume the snapshot later, but Movie World persistence and API contracts do not depend on one.

- **Movie Guide** owns high-level visual language and can use world references, facts, and locks as durable context.
- **Storyboard** owns scene records and attaches reusable world entities through `MovieWorldUsage`.
- **Shot Designer** adds shot-scoped usage records and receives scene-level plus shot-level world context in the generation input.
- **Movie Director** can treat active `MovieContinuityLock` records and default set variations as authoritative constraints without regenerating world identities.

## Bounded continuity projections

`MovieWorldContinuityProjector` is the single read-model seam for scene- and shot-specific World continuity. It resolves only the target's scene/shot usages, the referenced locations, sets and variations, props, applicable project/scene/shot facts, and active locks. It never serializes the complete World graph or project references. Hard bounds are enforced for usages (128), locations (16), sets (16), variations per set (8), props (32), facts (64), locks (64), and warnings (64).

`MovieWorldContinuitySnapshotDto` is an immutable record contract with `snapshotVersion`, a stable SHA-256 `snapshotHash`, target identifiers, bounded records for locations, sets/variations, props, facts, locks, and explainable warnings. Warning sources identify the originating World/continuity record and targets identify the scene/shot projection. Deterministic conflict codes include `locked_variation_mismatch`, `prop_state_inconsistency`, `conflicting_location_usage`, `conflicting_set_usage`, `conflicting_location_set_usage`, and `conflicting_locked_continuity_fact`.

The projection is available through project, scene, and shot World-continuity read routes. Shot production reads expose the same snapshot, Director context includes the target-shot snapshot before its versioned hash is stored, and scene/shot Generation Job input uses the snapshot JSON. Storyboard, keyframe, motion-preview, and render callers can therefore reference the bounded snapshot through the existing production shot read without introducing a second production or World hierarchy.

## Migration and tests

The additive EF migrations are:

- `20260926101007_AddMovieWorldFoundation`
- `20260926101303_AddMovieWorldVariations`

The second migration adds `MovieSetVariations`; both migrations are additive and do not alter provider execution tables. Focused tests in `apps/api.Tests/MovieWorldTests.cs` cover reusable location/set/prop attachments, variation states, references, facts, locks, generation snapshot propagation, cross-workspace isolation, and invalid cross-project usage.
