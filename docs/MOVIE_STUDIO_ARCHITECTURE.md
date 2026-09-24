# Taslim.ai Movie Studio Architecture

## Purpose and scope

Movie Studio establishes the production foundation for two workflows at `/create/movie`: **Quick Movie** and **Full Movie Project**. The feature saves a durable creative plan and connects it to a provider-neutral, restart-safe video execution layer. It supports continuity planning, per-shot/per-scene clip attachment, job accounting, and private video publication without emitting fake output when no vetted provider is configured.

The implementation intentionally does not reference or modify Batch 3.13 Social Media Studio code. It is isolated on `parallel/movie-studio` and is designed to be reconciled with concurrent work later.

## Workflow model

Quick Movie captures a title, description, duration, aspect ratio, style, language, optional existing Project, and additional instructions. The API persists a `MovieProject`, creates a `MovieClip`, and queues a durable `GenerationJob` with type `movie.quick.generate`. The worker submits through `IMovieVideoProvider`, persists the provider job identifier, polls with bounded retry/backoff, supports cancellation, retrieves the output as a stream, and publishes it through the existing private `StoredFile` → `Asset` path. When no provider is configured, the default implementation fails safely with `MOVIE_PROVIDER_UNAVAILABLE`; the plan, clip, usage transaction, and job history remain available for continuation.

Full Movie Project creates a durable planning workspace. If the user does not select an existing Project, the API creates a Taslim Project with type `Movie`. The Movie Project owns a Movie Guide / Continuity Guide and collections for scenes, characters, locations, shots, clips, and assemblies. The first UI exposes the guide and starter planning columns for scenes, characters, and locations. Shot, clip, and assembly persistence is present for future studio stages.

## Domain and persistence

Movie-specific tables are additive and live beside the existing Projects, Assets, Stored Files, Generation Jobs, and Usage Transactions. This keeps video-production structure separate from generic Project text while preserving the existing workspace access boundary.

| Entity | Role | Existing-system links |
| --- | --- | --- |
| `MovieProject` | Workflow root and creative brief | Workspace, optional Project, creator |
| `MovieContinuityGuide` | Visual, camera, lighting, sound, and continuity rules | One-to-one with Movie Project |
| `MovieScene` | Ordered story unit with narration and dialogue | Movie Project |
| `MovieCharacter` | Durable character identity and continuity notes | Movie Project, optional Asset reference |
| `MovieLocation` | Durable location identity and visual continuity | Movie Project, optional Asset reference |
| `MovieShot` | Shot description, framing, motion, narration, dialogue | Scene |
| `MovieClip` | Provider output and continuity attachment | Project, Scene, Shot, Generation Job, Asset, Stored File |
| `MovieAssembly` | Future final-assembly record | Project, Generation Job, Asset |

The EF-generated migration `20260923153752_AddMovieStudioFoundation` creates the planning tables. The additive migration `20260924012646_AddMovieVideoProviderExecution` adds continuity snapshots, scene clip links, and the durable provider execution table. Existing private storage remains the boundary for actual media files: `StoredFile` carries the storage key and provider, while `Asset` is the user-facing library record.

The schema does not store public provider URLs as final assets. Provider identifiers and metadata are retained as nullable fields so a configured provider can be reconciled with Taslim-owned private storage later.

## Generation jobs and usage accounting

Movie operations use the existing durable `GenerationJob` queue. The supported job types are `movie.quick.generate`, `movie.clip.generate`, and `movie.assembly`. Quick Movie creates a project-level clip; authenticated scene-level and shot-level generation routes create `movie.clip.generate` jobs that retain their scene/shot links and continuity snapshot. Assembly remains a reserved future stage.

`GenerationJobUsageService` maps every movie job type to `UsageFeature.Movie`. This preserves the repository's existing feature naming convention and allows pending, failed, cancelled, and completed transactions to appear in existing usage reporting without a new accounting subsystem.

The current provider registration is `UnavailableMovieVideoProvider`. It is explicit rather than a mock: it reports `IsAvailable = false` and throws `MovieProviderUnavailableException`. This prevents a successful-looking fake clip from entering Assets or private storage. A real adapter is intentionally not included because this baseline has no verified video API endpoint and no provider credentials/configuration.

## Provider-neutral boundary

The production integration point is:

```csharp
public interface IMovieVideoProvider
{
    string Key { get; }
    bool IsAvailable { get; }
    IReadOnlyCollection<string> SupportedOperations { get; }
    Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken);
    Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken);
    Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken);
    Task CancelAsync(string providerJobId, CancellationToken cancellationToken);
}
```

The request carries a stable generation-job identity, operation, creative brief, duration, aspect ratio, style, language, additional instructions, continuity-guide JSON, and optional scene/shot JSON. Provider job identifiers, attempt counts, poll counts, status, and next-poll state are stored in the server-only `MovieVideoProviderExecutions` table. A production provider implementation can be registered through dependency injection without changing the Movie Studio API contract. Provider/model/cost internals are not returned in user-facing Movie DTOs or result JSON.

## API surface

All endpoints require authentication and workspace membership. Mutating endpoints use the existing antiforgery middleware.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/movie-studio/provider` | Report provider readiness and supported operations |
| `POST /api/movie-studio/projects` | Create Quick Movie or Full Movie Project; Quick also queues a generation job |
| `GET /api/movie-studio/projects/{id}` | Load the durable plan and guide |
| `PATCH /api/movie-studio/projects/{id}/guide` | Update continuity-guide fields |
| `POST /api/movie-studio/projects/{id}/scenes` | Add an ordered scene |
| `POST /api/movie-studio/projects/{id}/characters` | Add a character record |
| `POST /api/movie-studio/projects/{id}/locations` | Add a location record |
| `POST /api/movie-studio/scenes/{sceneId}/shots` | Add an ordered shot with narration/dialogue and continuity fields |
| `POST /api/movie-studio/projects/{id}/scenes/{sceneId}/generate` | Queue a provider-neutral scene clip job |
| `POST /api/movie-studio/shots/{shotId}/generate` | Queue a provider-neutral shot clip job |

## UI foundation

The `/create/movie` page follows the existing Taslim studio visual language while using a cinematic dark hero, warm amber production accents, restrained card surfaces, and a responsive planning board. The first screen presents two workflow tabs. Quick Movie keeps the form compact and provider status visible. Full Movie Project adds the Movie Guide fields and, after creation, shows an editable production board for scenes, characters, and locations.

The page uses the existing `LocaleProvider`, whose `localeDirection` switches Arabic and Kurdish to RTL. Movie strings are provided for English, Arabic, and Kurdish. The layout uses document direction rather than hard-coded left-to-right assumptions, and the stylesheet includes reduced-motion behavior through the existing global rule.

## Shared-file change log

The following shared files were changed only where integration was required. The feature-specific implementation is isolated under `apps/api/Movies`, `apps/api/Controllers/MovieStudioController.cs`, `apps/web/src/components/MovieStudioView.tsx`, and the new route.

| Shared file | Change | Reconciliation note |
| --- | --- | --- |
| `apps/api/Domain/Entities.cs` | Added movie job types and movie failure codes; reused existing `UsageFeature.Movie` | Preserve the movie constants when other generation types are added |
| `apps/api/Persistence/TaslimDbContext.cs` | Added movie DbSets and EF relationships | Additive model configuration; resolve any concurrent migration snapshot changes together |
| `apps/api/Program.cs` | Registered Movie Studio service, unconfigured provider, and handler | Replace only the provider registration when a production provider is enabled |
| `apps/api/Generation/GenerationJobExecution.cs` | Mapped movie usage, cancellation, failure codes, and safe messages | Keep movie mapping adjacent to other job-type mappings |
| `apps/web/src/lib/api.ts` | Added Movie Studio types and API methods | Merge with any concurrent client type additions without changing existing method behavior |
| `apps/web/src/lib/data.ts` | Added Movie Studio navigation and Media → Movie route | Keep Social Media routing separate |
| `apps/web/src/components/AppShell.tsx` | Added Movie Studio to mobile navigation whitelist | Desktop navigation remains limited to the existing first six links |
| `apps/web/src/lib/i18n.ts` | Added EN/AR/KU Movie Studio strings | Preserve all three locale keys during future translation edits |
| `apps/web/src/app/globals.css` | Added scoped Movie Studio styles and responsive rules | Styles are prefixed with `movie-` to reduce collision risk |

No unfinished 3.13 code is imported, called, or modified.

## Migration and operations

The migrations are `AddMovieStudioFoundation` and `AddMovieVideoProviderExecution`; they should be applied through the repository's existing production advisory-lock migration runner. The second migration is required because provider job identifiers, polling state, retries, and continuity snapshots must survive an API restart. The EF migration designers and `TaslimDbContextModelSnapshot.cs` are included. The API build completed with zero warnings and errors, `dotnet ef migrations list` discovers the new migration, and an offline migration script contains `MovieVideoProviderExecutions`, `ContinuitySnapshotJson`, and the scene clip foreign key. The local PostgreSQL database was not running, so applied-versus-pending status could not be queried.

The web package has a focused `npm run lint`, test, and `npm run build` validation path. The UI does not require a provider to render, create a durable plan, or expose the provider readiness state. Production video output requires the existing private R2 configuration (`Files:StorageProvider=S3Compatible`, HTTPS `Files:S3Endpoint`, `Files:S3Region=auto`, bucket, access key, and secret) and a separately reviewed `IMovieVideoProvider` registration. No movie vendor endpoint or credential is present in this branch, so the default behavior is safely unavailable rather than simulated.

## Integration conflicts and boundaries

There are no expected conflicts with Batch 3.13 because no Social Media Studio file is referenced. If another branch changes the EF model snapshot concurrently, reconcile that generated file with the included Movie Studio snapshot changes. A future provider branch should replace `UnavailableMovieVideoProvider` with a real, verified `IMovieVideoProvider` implementation and configure only API-side credentials; it should not bypass the existing Generation Job, Asset, Stored File, Activity Center, or Usage Transaction boundaries. Customer charge remains zero; authoritative provider cost, when returned by the adapter, is retained only in the existing internal usage ledger.

## References

[1]: docs/GENERATION_JOBS_ARCHITECTURE.md "Taslim.ai Generation Jobs Architecture"
[2]: docs/ASSET_ARCHITECTURE.md "Taslim.ai Asset Architecture"
[3]: docs/USAGE_ACCOUNTING_ARCHITECTURE.md "Taslim.ai Usage Accounting Architecture"
[4]: docs/ARCHITECTURE.md "Taslim.ai System Architecture"
