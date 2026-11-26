using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Concurrency coverage for the durable final-assembly idempotency boundary.
/// The service replays a committed canonical assembly when a concurrent
/// identical submission loses the race on the unique
/// <c>IX_MovieAssemblies_MovieProjectId_IdempotencyKey</c> index, so the
/// recovery hinges on one predicate:
/// <see cref="MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation"/>.
/// The existing unit case feeds that predicate synthetic SQLite/PostgreSQL
/// strings, which proves nothing about what the real provider throws for this
/// exact index, and no case proves that an unrelated conflict on the same table
/// still escapes the <c>when</c> filter. These tests drive the genuine EF Core
/// SQLite stack: every submission inserts into the real index, so a classifier
/// that failed to recognize the provider's actual message would surface the raw
/// database error to the customer instead of replaying the winner.
/// Provider-free: no video provider, no FFmpeg, no network, no charges.
/// </summary>
[Collection("MovieFinalAssemblyAcceptance")]
public sealed class MovieFinalAssemblyIdempotencyRaceTests : IClassFixture<MovieFinalAssemblyApiFactory>
{
    private const int ConcurrentIdenticalSubmissions = 4;

    private readonly MovieFinalAssemblyApiFactory factory;

    public MovieFinalAssemblyIdempotencyRaceTests(MovieFinalAssemblyApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// The race itself. Concurrent identical submissions each pass the initial
    /// lookup, so the (MovieProjectId, IdempotencyKey) index is the only thing
    /// that decides the winner. Exactly one insert may persist, and every losing
    /// insert must be classified as replayable: that classification is what
    /// converts the unique violation into a returned canonical assembly rather
    /// than a raw persistence failure.
    /// </summary>
    [Fact]
    public async Task Concurrent_identical_submissions_race_on_the_durable_index_and_every_loser_is_replayable()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Concurrent final assembly replay");
        var movieProjectId = fixture.Project.Project.Id;
        var userId = fixture.Owner.User.Id;
        const string idempotencyKey = "assembly:concurrent-identical";

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrentIdenticalSubmissions)
            .Select(index => TryInsertAsync(movieProjectId, userId, idempotencyKey, index)));

        var accepted = results.Where(item => item.Succeeded).ToList();
        var conflicts = results.Where(item => item.Conflict is not null).ToList();

        Assert.Equal(1, accepted.Count);
        Assert.Equal(ConcurrentIdenticalSubmissions - 1, conflicts.Count);

        // The predicate the production replay depends on, evaluated against the
        // provider's real exception rather than a hand-written string.
        Assert.All(conflicts, item => Assert.True(
            MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation(item.Conflict!),
            $"A real duplicate-key failure on the assembly idempotency index must replay, " +
            $"but the classifier rejected: {item.Conflict}"));

        // The boundary is durable, not merely in-memory: the losers never leave a
        // second canonical row behind.
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var committed = await db.MovieAssemblies.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId && item.IdempotencyKey == idempotencyKey)
            .ToListAsync();
        Assert.Single(committed);
        Assert.Equal(accepted[0].AssemblyId, committed[0].Id);
    }

    /// <summary>
    /// The guard on the other side of the same <c>when</c> filter. A duplicate
    /// that violates an unrelated constraint on the very same table is not an
    /// idempotency replay, and must keep propagating so a genuine persistence
    /// bug cannot be silently reported to the customer as a successful
    /// "replayed" assembly.
    /// </summary>
    [Fact]
    public async Task Unrelated_assembly_conflict_stays_outside_the_idempotency_replay()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Unrelated assembly conflict");
        var movieProjectId = fixture.Project.Project.Id;
        var userId = fixture.Owner.User.Id;

        // Same primary key, different idempotency keys: the collision is on the
        // identity of the row, not on the idempotency boundary.
        var sharedId = Guid.NewGuid();
        var first = await TryInsertAsync(movieProjectId, userId, "assembly:identity-a", 0, sharedId);
        Assert.True(first.Succeeded);

        var collision = await TryInsertAsync(movieProjectId, userId, "assembly:identity-b", 1, sharedId);

        Assert.NotNull(collision.Conflict);
        Assert.False(MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation(collision.Conflict!));

        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
        // The rejected write must leave nothing behind: the loser surfaced as a
        // persistence failure rather than committing a second, mislabeled row.
        Assert.Equal(1, await db.MovieAssemblies.AsNoTracking()
            .CountAsync(item => item.MovieProjectId == movieProjectId));
    }

    private async Task<InsertOutcome> TryInsertAsync(
        Guid movieProjectId,
        Guid userId,
        string idempotencyKey,
        int ordinal,
        Guid? assemblyId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        var id = assemblyId ?? Guid.NewGuid();
        db.MovieAssemblies.Add(new MovieAssembly
        {
            Id = id,
            MovieProjectId = movieProjectId,
            RequestedByUserId = userId,
            Status = MovieAssemblyStatuses.Planned,
            OutputFormat = "mp4",
            ResolutionProfile = MovieFinalAssemblyProfiles.Uhd4K,
            OutputWidth = 3_840,
            OutputHeight = 2_160,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = $"fingerprint-{ordinal}",
            ProgressPercent = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync();
            return new InsertOutcome(Succeeded: true, Conflict: null, AssemblyId: id);
        }
        catch (DbUpdateException exception)
        {
            return new InsertOutcome(Succeeded: false, Conflict: exception, AssemblyId: id);
        }
    }

    private sealed record InsertOutcome(bool Succeeded, DbUpdateException? Conflict, Guid AssemblyId);
}
