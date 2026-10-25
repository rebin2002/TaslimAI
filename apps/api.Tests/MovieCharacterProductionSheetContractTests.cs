using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCharacterProductionSheetContractTests
{
    [Fact]
    public void Rejects_duplicate_identity_reference_assets()
    {
        var asset = Guid.NewGuid();
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = asset,
            BodyReferenceAssetId = asset,
        }));

        Assert.Equal("DUPLICATE_REFERENCE_ASSET", exception.Code);
    }

    [Fact]
    public void Approval_requires_all_five_identity_slots()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = Guid.NewGuid(),
            BodyReferenceAssetId = Guid.NewGuid(),
            FrontReferenceAssetId = Guid.NewGuid(),
            SideReferenceAssetId = Guid.NewGuid(),
        }, requireComplete: true));

        Assert.Equal("INCOMPLETE_REFERENCE_SHEET", exception.Code);
    }

    [Fact]
    public void Look_keys_are_unique_within_a_version()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            Looks =
            [
                new MovieCharacterProductionSheetLookRequest("hero", "Hero", Wardrobe: "Coat"),
                new MovieCharacterProductionSheetLookRequest("HERO", "Hero alternate", Wardrobe: "Jacket"),
            ],
        }));

        Assert.Equal("DUPLICATE_LOOK_KEY", exception.Code);
    }

    [Fact]
    public async Task Version_allocation_conflict_reloads_canonical_sheet_and_retries()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-character-sheet-{Guid.NewGuid():N}.db");
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using var seedDb = new TaslimDbContext(seedOptions);
            await seedDb.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(seedDb);
            var seedService = CreateService(seedDb);
            var initial = await seedService.SaveDraftAsync(seed.UserId, seed.CharacterId, new MovieCharacterProductionSheetRequest
            {
                ProvenanceNote = "initial",
            });
            Assert.NotNull(initial);
            Assert.Equal(1, initial!.CurrentVersionNumber);

            var retryOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .AddInterceptors(new VersionAllocationConflictInterceptor())
                .Options;
            await using var retryDb = new TaslimDbContext(retryOptions);
            var result = await CreateService(retryDb).SaveDraftAsync(seed.UserId, seed.CharacterId, new MovieCharacterProductionSheetRequest
            {
                ProvenanceNote = "retry-after-conflict",
            });
            Assert.NotNull(result);
            Assert.Equal(2, result!.CurrentVersionNumber);

            await using var verifyDb = new TaslimDbContext(seedOptions);
            var versions = await verifyDb.MovieCharacterProductionSheetVersions
                .AsNoTracking()
                .Where(item => item.MovieCharacterProductionSheetId == result.Id)
                .OrderBy(item => item.VersionNumber)
                .ToListAsync();
            Assert.Equal(new[] { 1, 2 }, versions.Select(item => item.VersionNumber));
            Assert.Contains(versions, item => item.ProvenanceJson.Contains("retry-after-conflict", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static MovieCharacterProductionSheetService CreateService(TaslimDbContext db) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static async Task<Seed> SeedAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "character-sheet-owner",
            NormalizedUserName = "CHARACTER-SHEET-OWNER",
            Email = "character-sheet-owner@example.com",
            NormalizedEmail = "CHARACTER-SHEET-OWNER@EXAMPLE.COM",
            DisplayName = "Character Sheet Owner",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Character Sheet Workspace",
            Slug = $"character-sheet-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now,
        });
        var movie = new MovieProject
        {
            Id = movieProjectId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Character Sheet Film",
            Description = "A deterministic character-sheet race fixture.",
            DurationSeconds = 30,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            Characters =
            [
                new MovieCharacter
                {
                    Id = characterId,
                    Name = "Mara",
                    Description = "A continuity fixture character.",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };
        db.MovieProjects.Add(movie);
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId,
            Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(userId, characterId);
    }

    private sealed class VersionAllocationConflictInterceptor : SaveChangesInterceptor
    {
        private int injected;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref injected, 1) == 0)
                throw new DbUpdateException("UNIQUE constraint failed: MovieCharacterProductionSheetVersions.MovieCharacterProductionSheetId, MovieCharacterProductionSheetVersions.VersionNumber");

            return ValueTask.FromResult(result);
        }
    }

    private sealed record Seed(Guid UserId, Guid CharacterId);
}
