using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
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

public sealed class ProviderHealthServiceTests
{
    [Theory]
    [InlineData(-1, null, "recent_operational_failure")]
    [InlineData(-6, null, "operational")]
    [InlineData(-4, null, "operational")]
    [InlineData(-6, -1, "recent_operational_failure")]
    [InlineData(-6, -5, "operational")]
    public async Task Status_follows_the_latest_job_or_usage_event(
        int jobFailureOffsetMinutes, int? usageFailureOffsetMinutes, string expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var jobFailedAt = now.AddMinutes(jobFailureOffsetMinutes);
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "provider-health@example.com",
            NormalizedUserName = "PROVIDER-HEALTH@EXAMPLE.COM",
            Email = "provider-health@example.com",
            NormalizedEmail = "PROVIDER-HEALTH@EXAMPLE.COM",
            DisplayName = "Provider Health Test",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Provider Health Workspace",
            Slug = $"provider-health-{workspaceId:N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        db.GenerationJobs.AddRange(
            new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.ImageGenerate,
                Status = GenerationJobStatus.Succeeded,
                CreatedAt = now.AddMinutes(-5),
                CompletedAt = now.AddMinutes(-4),
            },
            new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.ImageGenerate,
                Status = GenerationJobStatus.Failed,
                ErrorCode = GenerationJobErrorCodes.ImageProviderUnavailable,
                CreatedAt = jobFailedAt.AddMinutes(-1),
                FailedAt = jobFailedAt,
            });
        if (usageFailureOffsetMinutes is int offset)
        {
            var usageFailedAt = now.AddMinutes(offset);
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                RequestId = $"provider-health-{Guid.NewGuid():N}",
                Feature = UsageFeature.Image,
                Provider = "test-provider",
                Model = "test-model",
                Status = UsageTransactionStatus.Failed,
                FailureCode = "IMAGE_PROVIDER_TIMEOUT",
                CreatedAt = usageFailedAt,
                CompletedAt = usageFailedAt,
                Currency = "USD",
            });
        }
        await db.SaveChangesAsync();

        var service = new ProviderHealthService(
            db,
            Options.Create(new AiOptions
            {
                AllowMockProvider = false,
                OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "synthetic-test-key" },
            }),
            Options.Create(new ImageGenerationOptions { Enabled = true, ProviderKey = "openai" }),
            Options.Create(new DocumentGenerationOptions { Enabled = false }),
            Options.Create(new PresentationGenerationOptions { Enabled = false }),
            Options.Create(new ResearchGenerationOptions { Enabled = false }),
            Options.Create(new SocialGenerationOptions { Enabled = false }),
            Options.Create(new MusicGenerationOptions { Enabled = false }),
            Options.Create(new VoiceGenerationOptions { Enabled = false }),
            Options.Create(new MovieVideoOptions { Enabled = false }),
            Array.Empty<IMovieVideoProvider>());

        var health = await service.GetAsync((now.AddHours(-1), now.AddHours(1)), CancellationToken.None);
        var image = Assert.Single(health, item => item.Key == "openai-image");

        Assert.Equal(expectedStatus, image.Status);
        Assert.Equal(jobFailedAt, image.LastFailureAt);
        Assert.Equal(1, image.RecentSuccessCount);
        Assert.Equal(1, image.RecentFailureCount);
        Assert.Equal(GenerationJobErrorCodes.ImageProviderUnavailable, image.LastFailureCode);
    }
}
