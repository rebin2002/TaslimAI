using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Documents;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Movies;
using Taslim.Api.Music;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Taslim.Api.Research;
using Taslim.Api.Social;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Contract tests for the admin-only provider health metadata surface
/// (ProviderHealthService), which feeds the Operations dashboard provider control
/// plane. The dashboard claims a reported failure carries sanitized evidence, so the
/// last-failure code, the last-failure timestamp and the recent-failure list must
/// describe the same observed failure instead of a code invented for a job row that
/// never existed. These tests are provider-free: no provider call, no credential and
/// no user data are involved.
/// </summary>
public sealed class AdminProviderHealthMetadataTests
{
    private static readonly DateTime RangeAnchor = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Ledger_only_failure_reports_the_observed_code_and_timestamp_not_an_invented_one()
    {
        // The chat provider is the always-reachable case: it has no generation job
        // types, so its failure evidence can only come from the usage ledger.
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: true);
        var failedAt = RangeAnchor.AddMinutes(-5);
        db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Chat, "CHAT_PROVIDER_UNAVAILABLE", UsageTransactionStatus.Failed, failedAt));
        await db.SaveChangesAsync();

        var chat = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "chat");

        Assert.Equal("recent_operational_failure", chat.Status);
        Assert.Equal(1, chat.RecentFailureCount);
        // Before the fix this was the bounded fallback code, presented as if a job had
        // failed with it, while LastFailureAt was absent and no evidence was listed.
        Assert.Equal("CHAT_PROVIDER_UNAVAILABLE", chat.LastFailureCode);
        Assert.Equal(failedAt, chat.LastFailureAt);
        var evidence = Assert.Single(chat.RecentFailures);
        Assert.Equal("chat", evidence.JobType);
        Assert.Equal("CHAT_PROVIDER_UNAVAILABLE", evidence.ErrorCode);
        Assert.Equal(failedAt, evidence.OccurredAt);
    }

    [Fact]
    public async Task Provider_without_an_observed_failure_keeps_both_last_failure_fields_absent()
    {
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: true);
        db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Chat, null, UsageTransactionStatus.Completed, RangeAnchor.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var chat = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "chat");

        Assert.Equal("operational", chat.Status);
        Assert.Equal(0, chat.RecentFailureCount);
        Assert.Null(chat.LastFailureCode);
        Assert.Null(chat.LastFailureAt);
        Assert.Empty(chat.RecentFailures);
    }

    [Fact]
    public async Task Job_failure_evidence_stays_authoritative_over_ledger_evidence()
    {
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: false);
        var jobFailedAt = RangeAnchor.AddMinutes(-20);
        db.GenerationJobs.Add(NewJob(workspaceId, userId, GenerationJobTypes.ImageGenerate, GenerationJobStatus.Failed, "IMAGE_PROVIDER_RATE_LIMITED", RangeAnchor.AddMinutes(-30), jobFailedAt));
        // A later ledger failure must not displace the job evidence, matching the
        // jobs-first precedence the surrounding counts already use.
        db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Image, "IMAGE_PROVIDER_TIMEOUT", UsageTransactionStatus.Failed, RangeAnchor.AddMinutes(-10)));
        await db.SaveChangesAsync();

        var image = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "openai-image");

        Assert.Equal(1, image.RecentFailureCount);
        Assert.Equal("IMAGE_PROVIDER_RATE_LIMITED", image.LastFailureCode);
        Assert.Equal(jobFailedAt, image.LastFailureAt);
        var evidence = Assert.Single(image.RecentFailures);
        Assert.Equal(GenerationJobTypes.ImageGenerate, evidence.JobType);
        Assert.Equal("IMAGE_PROVIDER_RATE_LIMITED", evidence.ErrorCode);
        Assert.Equal(1, image.TimeoutEventCount);
    }

    [Fact]
    public async Task Ledger_failure_evidence_is_bounded_and_newest_first()
    {
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: true);
        for (var index = 0; index < 10; index++)
        {
            db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Chat, $"CHAT_PROVIDER_TRANSIENT_{index:D2}", UsageTransactionStatus.Failed, RangeAnchor.AddMinutes(-index - 1)));
        }
        await db.SaveChangesAsync();

        var chat = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "chat");

        // The evidence list is capped at eight while the count stays unbounded, so
        // operators still see the true failure volume.
        Assert.Equal(10, chat.RecentFailureCount);
        Assert.Equal(8, chat.RecentFailures.Count);
        Assert.Equal("CHAT_PROVIDER_TRANSIENT_00", chat.LastFailureCode);
        Assert.Equal(RangeAnchor.AddMinutes(-1), chat.LastFailureAt);
        var newestFirst = chat.RecentFailures.Select(item => item.OccurredAt).OrderByDescending(item => item).ToArray();
        Assert.Equal(newestFirst, chat.RecentFailures.Select(item => item.OccurredAt).ToArray());
        Assert.Equal("CHAT_PROVIDER_TRANSIENT_00", chat.RecentFailures[0].ErrorCode);
    }

    [Fact]
    public async Task A_code_that_is_not_a_bounded_token_is_redacted_instead_of_scrubbed()
    {
        // The persisted code is the last thing that reaches the dashboard. Removing
        // delimiters from an arbitrary string leaves the payload readable, so a value
        // that is not already a bounded token must be replaced by the stable fallback
        // code instead of being shown in scrubbed form.
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: true);
        var leaked = "MUBERT_PROVIDER_UNAVAILABLE https://api.mubert.com/v2/GetTask auth=supersecrettoken subscriber@example.com";
        db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Chat, leaked, UsageTransactionStatus.Failed, RangeAnchor.AddMinutes(-2)));
        await db.SaveChangesAsync();

        var chat = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "chat");

        Assert.Equal("GENERATION_FAILURE", chat.LastFailureCode);
        Assert.NotNull(chat.LastFailureAt);
        var evidence = Assert.Single(chat.RecentFailures);
        Assert.Equal("GENERATION_FAILURE", evidence.ErrorCode);
        foreach (var surfaced in new[] { chat.LastFailureCode!, evidence.ErrorCode })
        {
            Assert.True(surfaced.Length <= 96, "a redacted code exceeded the 96 character bound");
            Assert.DoesNotContain("supersecrettoken", surfaced, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mubert", surfaced, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.com", surfaced, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_bounded_token_code_is_surfaced_without_rewriting()
    {
        // Fail-closed redaction must not damage the real code vocabulary, because the
        // bounded categories derived from these codes drive the operator counters.
        var (service, db, workspaceId, userId) = await SetupAsync(mockChat: true);
        db.UsageTransactions.Add(NewUsage(workspaceId, userId, UsageFeature.Chat, "CHAT_PROVIDER_RATE_LIMITED", UsageTransactionStatus.Failed, RangeAnchor.AddMinutes(-2)));
        await db.SaveChangesAsync();

        var chat = Single(await service.GetAsync((RangeAnchor.AddDays(-1), RangeAnchor.AddDays(1)), CancellationToken.None), "chat");

        Assert.Equal("CHAT_PROVIDER_RATE_LIMITED", chat.LastFailureCode);
        Assert.Equal(1, chat.RateLimitEventCount);
    }

    private static async Task<(ProviderHealthService Service, TaslimDbContext Db, Guid WorkspaceId, Guid UserId)> SetupAsync(bool mockChat)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Provider Health Workspace",
            Slug = $"provider-health-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = RangeAnchor,
            UpdatedAt = RangeAnchor,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"provider-health-{Guid.NewGuid():N}",
            Email = $"provider-health-{Guid.NewGuid():N}@example.test",
            DisplayName = "Provider Health Test",
            CreatedAt = RangeAnchor,
            UpdatedAt = RangeAnchor,
        };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new ProviderHealthService(
            db,
            // Synthetic, non-functional configuration only: this service reads
            // configuration to report readiness and never contacts a provider.
            Options.Create(new AiOptions
            {
                AllowMockProvider = mockChat,
                OpenAI = new OpenAiOptions { Enabled = !mockChat, ApiKey = "test-key" },
            }),
            Options.Create(new ImageGenerationOptions { Enabled = true, ProviderKey = "openai" }),
            Options.Create(new DocumentGenerationOptions()),
            Options.Create(new PresentationGenerationOptions()),
            Options.Create(new ResearchGenerationOptions()),
            Options.Create(new SocialGenerationOptions()),
            Options.Create(new MusicGenerationOptions()),
            Options.Create(new VoiceGenerationOptions()),
            Options.Create(new MovieVideoOptions()),
            Array.Empty<IMovieVideoProvider>());
        return (service, db, workspace.Id, user.Id);
    }

    private static GenerationJob NewJob(Guid workspaceId, Guid userId, string jobType, GenerationJobStatus status, string? errorCode, DateTime createdAt, DateTime? failedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        CreatedByUserId = userId,
        JobType = jobType,
        Status = status,
        ErrorCode = errorCode,
        CreatedAt = createdAt,
        FailedAt = failedAt,
        CompletedAt = status == GenerationJobStatus.Succeeded ? createdAt : null,
    };

    private static UsageTransaction NewUsage(Guid workspaceId, Guid userId, UsageFeature feature, string? failureCode, UsageTransactionStatus status, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        RequestId = $"provider-health-{Guid.NewGuid():N}",
        Feature = feature,
        Provider = "test-provider",
        Model = "test-model",
        Status = status,
        FailureCode = failureCode,
        CreatedAt = at,
        CompletedAt = status == UsageTransactionStatus.Completed ? at : null,
        Currency = "USD",
    };

    private static AdminProviderHealthDto Single(IReadOnlyList<AdminProviderHealthDto> providers, string key) =>
        Assert.Single(providers, item => item.Key == key);
}
