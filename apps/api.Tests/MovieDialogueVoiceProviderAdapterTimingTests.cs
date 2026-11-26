using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceProviderAdapterTimingTests
{
    [Theory]
    [InlineData(2_001)]
    [InlineData(3_000)]
    public async Task Rejects_provider_audio_that_exceeds_the_authored_dialogue_window(int reportedDuration)
    {
        var adapter = CreateAdapter(reportedDuration);
        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(() => adapter.GenerateAsync(CreateRequest()));
    }

    [Theory]
    [InlineData(1_999, 1_999)]
    [InlineData(2_000, 2_000)]
    [InlineData(null, 2_000)]
    [InlineData(0, 2_000)]
    [InlineData(-1, 2_000)]
    public async Task Preserves_in_window_duration_or_falls_back_to_the_authored_window(int? reportedDuration, int expectedDuration)
    {
        var result = await CreateAdapter(reportedDuration).GenerateAsync(CreateRequest());
        Assert.Equal(expectedDuration, result.DurationMilliseconds);
    }

    [Fact]
    public async Task Exact_two_second_dialogue_output_remains_publishable_with_its_provider_cost()
    {
        var result = await CreateHandler(CreateAdapter(2_000, 0.375m)).ExecuteAsync(
            CreateJob(1_000, 3_000), new Progress<int>(), CancellationToken.None);

        Assert.Equal(2_000, ReadDuration(result.ResultJson));
        var output = Assert.Single(result.Outputs);
        Assert.NotNull(output.FileArtifact);
        Assert.Equal(2_000, ReadDuration(output.MetadataJson!));
        Assert.Equal(0.375m, result.Usage!.ActualCost);
    }

    [Fact]
    public async Task One_millisecond_overrun_keeps_known_cost_nonbillable_and_replay_safe()
    {
        var exception = await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(() =>
            CreateHandler(CreateAdapter(2_001, 0.375m)).ExecuteAsync(
                CreateJob(1_000, 3_000), new Progress<int>(), CancellationToken.None));
        var failureUsage = GenerationJobWorker.ResolveMovieDialogueFailureUsage(exception);

        Assert.NotNull(failureUsage);
        Assert.Equal(0.375m, failureUsage!.ActualCost);
        Assert.Equal(0.41m, failureUsage.EstimatedCost);
        Assert.Equal("USD", failureUsage.Currency);

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = "Test", Slug = $"test-{workspaceId:N}", Type = WorkspaceType.Personal,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "dialogue-cost@example.com", NormalizedUserName = "DIALOGUE-COST@EXAMPLE.COM",
            Email = "dialogue-cost@example.com", NormalizedEmail = "DIALOGUE-COST@EXAMPLE.COM", DisplayName = "Test",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId,
            JobType = GenerationJobTypes.MovieDialogueVoiceGenerate, InputJson = "{}", CreatedAt = DateTime.UtcNow,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync();
        var ledger = new UsageLedgerService(
            db,
            new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
            new SafeUsageChargingService(),
            new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
            NullLogger<UsageLedgerService>.Instance);
        var usage = new GenerationJobUsageService(ledger);

        var transaction = await usage.BeginAsync(job, estimatedProviderCostUsd: 0.41m);
        await usage.FailAsync(transaction, GenerationJobErrorCodes.MovieDialogueVoiceOutputInvalid, failureUsage);
        var firstRecordedAt = transaction.ActualRecordedAt;
        var replayed = await usage.BeginAsync(job, estimatedProviderCostUsd: 0.41m);
        Assert.Equal(transaction.Id, replayed.Id);
        await usage.FailAsync(replayed, GenerationJobErrorCodes.MovieDialogueVoiceOutputInvalid, failureUsage);

        Assert.Equal(UsageTransactionStatus.Failed, transaction.Status);
        Assert.True(transaction.ProviderCostKnown);
        Assert.Equal(0.375m, transaction.ProviderCostUsd);
        Assert.False(transaction.IsBillable);
        Assert.Null(transaction.BillableAt);
        Assert.Equal(0m, transaction.ChargedAmount);
        Assert.Equal(firstRecordedAt, transaction.ActualRecordedAt);
        Assert.Single(await db.UsageTransactions.ToListAsync());
        Assert.Empty(await db.UsageTransactionAdjustments.ToListAsync());
    }

    private static MovieDialogueVoiceGenerationJobHandler CreateHandler(IMovieDialogueVoiceProvider provider) => new(
        [provider],
        Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake", MaxDurationMilliseconds = 10_000 }),
        NullLogger<MovieDialogueVoiceGenerationJobHandler>.Instance);

    private static MovieDialogueVoiceProviderAdapter CreateAdapter(int? reportedDuration, decimal? actualCostUsd = null) => new(
        new FixedDurationVoiceProvider(reportedDuration, actualCostUsd),
        Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake", MaxDurationMilliseconds = 10_000 }),
        Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "voice-test-model" }));

    private static GenerationJob CreateJob(int startMilliseconds, int endMilliseconds)
    {
        var input = new MovieDialogueVoiceInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello from Movie Studio.",
            MovieDialogueLanguages.English, startMilliseconds, endMilliseconds, null, Guid.NewGuid());
        return new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.MovieDialogueVoiceGenerate,
            InputJson = JsonSerializer.Serialize(input),
        };
    }

    private static int ReadDuration(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("durationMilliseconds").GetInt32();

    private static MovieDialogueVoiceProviderRequest CreateRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello from Movie Studio.",
        MovieDialogueLanguages.English, 1_000, 3_000, null, Guid.NewGuid());

    private sealed class FixedDurationVoiceProvider(int? durationMilliseconds, decimal? actualCostUsd) : IVoiceGenerationProvider
    {
        public string Key => "fake";

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VoiceProviderResult(
                new byte[] { 0x49, 0x44, 0x33, 0x04 },
                "audio/mpeg",
                "mp3",
                durationMilliseconds,
                null,
                new VoiceProviderUsage(
                    "voice-test-model", request.Text.Length, 4, actualCostUsd, 0,
                    Currency: "USD", CostBasis: UsageCostBasis.Actual, SafeMetadataJson: "{\"synthetic\":true}",
                    EstimatedCostUsd: actualCostUsd.HasValue ? 0.41m : null)));
        }
    }
}
