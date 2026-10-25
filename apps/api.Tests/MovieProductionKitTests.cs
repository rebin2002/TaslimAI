using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionKitTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieProductionKitTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Kit_references_locked_guide_and_canonical_character_without_copying_guide_content()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Production Kit Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        var character = await Send<MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new
        {
            name = "Mara", role = "Lead", description = "A careful investigator.", appearance = "Short dark hair", wardrobe = "Charcoal coat",
        });

        var beforeLock = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/revisions", new
        {
            references = new[] { new { referenceType = "character", sourceId = character.Id, label = "Lead", role = "protagonist", isRequired = true, provenanceJson = "{\"source\":\"cast\"}" } },
        });
        Assert.Equal(1, beforeLock.CurrentRevision!.RevisionNumber);
        Assert.Equal(MovieProductionKitStatuses.Draft, beforeLock.CurrentRevision.Status);
        Assert.False(beforeLock.Readiness.Ready);
        Assert.Contains("guide_not_locked", beforeLock.Readiness.Missing);
        Assert.Single(beforeLock.CurrentRevision.References);
        Assert.Equal(character.Id, beforeLock.CurrentRevision.References[0].SourceId);
        Assert.Equal(64, beforeLock.CurrentRevision.References[0].SourceHash.Length);
        Assert.DoesNotContain("visual_bible", JsonSerializer.Serialize(beforeLock), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", JsonSerializer.Serialize(beforeLock), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", JsonSerializer.Serialize(beforeLock), StringComparison.OrdinalIgnoreCase);

        await Send<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        var review = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/review", new { revisionNumber = 1 });
        Assert.Equal(MovieProductionKitStatuses.Review, review.CurrentRevision!.Status);
        Assert.True(review.Readiness.Ready);
        var approved = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/approve", new { revisionNumber = 1, note = "References are ready." });
        Assert.Equal(MovieProductionKitStatuses.Approved, approved.CurrentRevision!.Status);
        Assert.Equal(auth.User.Id, approved.CurrentRevision.ReviewedByUserId);
        var locked = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/lock", new { revisionNumber = 1 });
        Assert.Equal(MovieProductionKitStatuses.Locked, locked.CurrentRevision!.Status);
        Assert.Equal(1, locked.LockedRevisionNumber);
    }

    [Fact]
    public async Task Kit_rechecks_asset_readiness_before_approval_when_a_file_is_archived()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Production Kit Asset Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Production Kit Asset Readiness");
        await Send<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });

        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                ProjectId = movie.Project.ProjectId,
                OriginalFileName = "approved-reference.png",
                StoredFileName = "approved-reference.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 4,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/{fileId:N}.png",
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                CreatedAt = now,
                ProcessedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                ProjectId = movie.Project.ProjectId,
                CreatedByUserId = auth.User.Id,
                StoredFileId = fileId,
                Name = "Approved reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var draft = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/revisions", new
        {
            references = new[] { new { referenceType = "asset", sourceId = assetId, isRequired = true } },
        });
        Assert.True(draft.Readiness.Ready);
        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/review", new { revisionNumber = 1 });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.SingleAsync(item => item.Id == assetId);
            asset.Status = AssetStatus.Archived;
            asset.ArchivedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var current = await client.GetFromJsonAsync<MovieProductionKitDto>($"/api/movie-studio/projects/{movie.Project.Id}/production-kit");
        Assert.NotNull(current);
        Assert.False(current!.Readiness.Ready);
        Assert.Contains(MovieProductionKitReadinessCodes.ReferenceNotReady, current.Readiness.Missing);

        using var blocked = await SendRaw(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/approve", new { revisionNumber = 1 });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_KIT_NOT_READY", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kit_revision_is_append_only_and_locked_kit_rejects_new_revision()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Production Kit Revisioner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await Send<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/revisions", new { notes = "First hand-off" });
        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/review", new { revisionNumber = 1 });
        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/approve", new { revisionNumber = 1 });
        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/lock", new { revisionNumber = 1 });

        var blocked = await SendRaw(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/revisions", new { notes = "Must not mutate locked history" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_KIT_LOCKED", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/unlock", null);
        var second = await Send<MovieProductionKitDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/production-kit/revisions", new { notes = "Second hand-off" });
        Assert.Equal(2, second.CurrentRevision!.RevisionNumber);
        Assert.Equal(2, second.Revisions.Count);
        Assert.Equal("First hand-off", second.Revisions.Single(item => item.RevisionNumber == 1).Notes);
        Assert.Equal("Second hand-off", second.Revisions.Single(item => item.RevisionNumber == 2).Notes);
    }

    [Fact]
    public async Task Concurrent_revision_creation_retries_the_losing_revision_number_allocation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-production-kit-{Guid.NewGuid():N}.db");
        var barrier = new ProductionKitReadBarrier();
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using (var seedDb = new TaslimDbContext(seedOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                await SeedDirectAsync(seedDb);
            }

            async Task<MovieProductionKitDto?> CreateRevisionAsync(string notes)
            {
                var options = new DbContextOptionsBuilder<TaslimDbContext>()
                    .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                    .AddInterceptors(barrier)
                    .Options;
                await using var db = new TaslimDbContext(options);
                var seed = await db.MovieProjects.Select(item => new DirectProductionKitSeed(item.CreatedByUserId, item.Id)).SingleAsync();
                var service = CreateDirectService(db);
                return await service.CreateRevisionAsync(seed.UserId, seed.MovieProjectId, new MovieProductionKitRevisionRequest { Notes = notes }, CancellationToken.None);
            }

            var results = await Task.WhenAll(CreateRevisionAsync("Concurrent hand-off A"), CreateRevisionAsync("Concurrent hand-off B"));
            Assert.All(results, result => Assert.NotNull(result));

            await using var verifyDb = new TaslimDbContext(seedOptions);
            var kit = await verifyDb.MovieProductionKits
                .Include(item => item.Revisions)
                .SingleAsync();
            Assert.Equal(3, kit.CurrentRevisionNumber);
            Assert.Equal(new[] { 1, 2, 3 }, kit.Revisions.OrderBy(item => item.RevisionNumber).Select(item => item.RevisionNumber).ToArray());
            Assert.Equal(3, kit.Revisions.Count);
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    [Fact]
    public void Production_kit_retry_classifier_accepts_only_its_allocation_conflicts()
    {
        var sqliteConflict = new DbUpdateException(
            "Production Kit revision insert failed.",
            new InvalidOperationException("SQLite Error 19: UNIQUE constraint failed: MovieProductionKitRevisions.MovieProductionKitId, MovieProductionKitRevisions.RevisionNumber"));
        var postgresConflict = new DbUpdateException(
            "Production Kit revision insert failed.",
            new InvalidOperationException("23505 duplicate key value violates unique constraint IX_MovieProductionKitRevisions_MovieProductionKitId_RevisionNu~"));
        var unrelatedConflict = new DbUpdateException(
            "Reference insert failed.",
            new InvalidOperationException("SQLite Error 19: UNIQUE constraint failed: MovieProductionKitReferences.Id"));

        Assert.True(MovieProductionKitService.IsProductionKitAllocationUniqueViolation(sqliteConflict));
        Assert.True(MovieProductionKitService.IsProductionKitAllocationUniqueViolation(postgresConflict));
        Assert.False(MovieProductionKitService.IsProductionKitAllocationUniqueViolation(unrelatedConflict));
    }

    [Fact]
    public async Task Kit_is_project_scoped_and_cross_project_sources_are_rejected()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Production Kit Isolation");
        var first = await CreateMovie(owner, auth.PersonalWorkspace.Id, "First");
        var second = await CreateMovie(owner, auth.PersonalWorkspace.Id, "Second");
        var character = await Send<MovieCharacterDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{second.Project.Id}/characters", new { name = "Private", description = "Other movie character." });
        var crossProject = await SendRaw(owner, HttpMethod.Post, $"/api/movie-studio/projects/{first.Project.Id}/production-kit/revisions", new
        {
            references = new[] { new { referenceType = "character", sourceId = character.Id, isRequired = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_KIT_REFERENCE_NOT_FOUND", await crossProject.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Production Kit Outsider");
        var read = await outsider.GetAsync($"/api/movie-studio/projects/{first.Project.Id}/production-kit");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName) =>
        await Send<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName, email = $"movie-kit-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en",
        });

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title = "Production Kit Movie") =>
        await Send<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId, mode = MovieProjectModes.Full, title, description = "A deterministic Production Kit test movie.", durationSeconds = 60,
            aspectRatio = "16:9", style = "cinematic", language = "en", visualLanguage = "35mm visual language", cameraLanguage = "Restrained camera",
        });

    private static async Task<T> Send<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendRaw(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendRaw(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static MovieProductionKitService CreateDirectService(TaslimDbContext db) =>
        new(db, new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db))));

    private static async Task<DirectProductionKitSeed> SeedDirectAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var guideRevisionId = Guid.NewGuid();
        var kitId = Guid.NewGuid();
        var kitRevisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var guideRevision = new MovieGuideRevision
        {
            Id = guideRevisionId, MovieContinuityGuideId = guideId, RevisionNumber = 1,
            CreatedByUserId = userId, CreatedAt = now,
        };
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "production-kit-race-owner", NormalizedUserName = "PRODUCTION-KIT-RACE-OWNER", DisplayName = "Production Kit Race Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Production Kit Race Workspace", Slug = $"production-kit-race-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Production Kit Race Movie",
            Description = "A deterministic concurrency fixture.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = guideId, CurrentRevisionNumber = 1, UpdatedAt = now, Revisions = [guideRevision] },
        });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now });
        db.MovieProductionKits.Add(new MovieProductionKit
        {
            Id = kitId, MovieProjectId = movieProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now,
            Revisions = [new MovieProductionKitRevision
            {
                Id = kitRevisionId, MovieProductionKitId = kitId, RevisionNumber = 1,
                SourceGuideRevisionId = guideRevisionId, SourceGuideRevisionNumber = 1,
                CreatedByUserId = userId, CreatedAt = now, SourceGuideHash = "seed", RevisionHash = "seed",
            }],
        });
        await db.SaveChangesAsync();
        return new DirectProductionKitSeed(userId, movieProjectId);
    }

    private sealed class ProductionKitReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsInitialKitRead(command))
            {
                if (Interlocked.Increment(ref arrivals) >= 2) released.TrySetResult(true);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }

        private static bool IsInitialKitRead(DbCommand command) =>
            command.CommandText.Contains("MovieProductionKits", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("MovieProductionKitRevisions", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("MovieProjectId", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DirectProductionKitSeed(Guid UserId, Guid MovieProjectId);
}
