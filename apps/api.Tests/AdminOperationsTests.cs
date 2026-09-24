using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AdminOperationsTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AdminOperationsTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Operations_dashboard_is_admin_only_and_uses_real_aggregate_data()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Operations Administrator");
        await AddAdminRole(auth.User.Email);
        var now = DateTime.UtcNow;
        var failedJobId = Guid.NewGuid();
        var runningJobId = Guid.NewGuid();
        var completedJobId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var file = new StoredFile
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id,
                OriginalFileName = "operations.json", StoredFileName = "operations.json", ContentType = "application/json",
                Extension = ".json", SizeBytes = 2_048, StorageProvider = FileStorageProviders.Local, StorageKey = $"operations/{Guid.NewGuid():N}",
                Status = StoredFileStatus.Ready, TextExtractionStatus = FileExtractionStatus.NotApplicable, CreatedAt = now,
            };
            var failedJob = new GenerationJob
            {
                Id = failedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.ImageGenerate, Status = GenerationJobStatus.Failed, Title = "Failed image",
                InputJson = "{}", ErrorCode = GenerationJobErrorCodes.ImageGenerationFailed, ErrorMessage = "Internal provider detail must not be returned.",
                CreatedAt = now.AddMinutes(-5), FailedAt = now.AddMinutes(-4), ConcurrencyToken = Guid.NewGuid(),
            };
            var runningJob = new GenerationJob
            {
                Id = runningJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.VoiceGenerate, Status = GenerationJobStatus.Running, Title = "Running voice",
                InputJson = "{}", ProgressPercent = 55, CreatedAt = now.AddMinutes(-3), QueuedAt = now.AddMinutes(-3), StartedAt = now.AddMinutes(-2), ConcurrencyToken = Guid.NewGuid(),
            };
            var completedJob = new GenerationJob
            {
                Id = completedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Succeeded, Title = "Completed document",
                InputJson = "{}", ProgressPercent = 100, CreatedAt = now.AddMinutes(-8), CompletedAt = now.AddMinutes(-1), ConcurrencyToken = Guid.NewGuid(),
            };
            db.StoredFiles.Add(file);
            db.Assets.Add(new Asset
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id, StoredFileId = file.Id,
                SourceGenerationJobId = completedJob.Id, Name = "Operations report", AssetType = AssetTypes.Document,
                MimeType = "application/json", Status = AssetStatus.Active, CreatedAt = now, UpdatedAt = now,
            });
            db.GenerationJobs.AddRange(failedJob, runningJob, completedJob);
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id, GenerationJobId = completedJob.Id,
                RequestId = $"admin-operations-{Guid.NewGuid():N}", Feature = UsageFeature.Document, Provider = "test-provider", Model = "test-model",
                Status = UsageTransactionStatus.Completed, InputTokens = 300, CachedInputTokens = 20, OutputTokens = 500,
                ImageInputTokens = 7, ImageOutputTokens = 11, ProviderCostUsd = 1.25m, ChargedAmount = 4.50m,
                Currency = "USD", CostBasis = UsageCostBasis.Actual, CreatedAt = now, CompletedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddDays(1).ToString("O"));
        var response = await client.GetAsync($"/api/admin/operations/dashboard?fromUtc={from}&toUtc={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var dashboard = JsonSerializer.Deserialize<AdminOperationsDashboardDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dashboard);
        Assert.Contains(dashboard!.Generation.ByStatus, item => item.Key == "Running" && item.Count >= 1);
        Assert.Contains(dashboard.Generation.ByStudio, item => item.Key == GenerationJobTypes.ImageGenerate && item.Count >= 1);
        Assert.Contains(dashboard.Generation.RunningJobs, item => item.JobId == runningJobId && item.ProgressPercent == 55);
        Assert.Contains(dashboard.Generation.RecentFailures, item => item.JobId == failedJobId && item.ErrorCode == GenerationJobErrorCodes.ImageGenerationFailed);
        Assert.True(dashboard.Usage.RequestCount >= 1);
        Assert.True(dashboard.Usage.ProviderCostUsd >= 1.25m);
        Assert.True(dashboard.Usage.CustomerChargesUsd >= 4.50m);
        Assert.Contains(dashboard.Usage.ByFeature, item => item.Feature == "Document" && item.InputTokens >= 300 && item.OutputTokens >= 500);
        Assert.True(dashboard.AssetsAndStorage.TotalStoredFiles >= 1);
        Assert.Contains(dashboard.AssetsAndStorage.AssetsByType, item => item.Key == AssetTypes.Document && item.Count >= 1);
        Assert.False(dashboard.Billing.CustomerChargingEnabled);
        Assert.Equal("unconfigured", dashboard.Billing.ConfiguredProvider);
        Assert.False(dashboard.Billing.PaymentProviderConfigured);
        Assert.True(dashboard.Signals.RunningJobCount >= 1);
        Assert.NotNull(dashboard.Signals.LastCompletedGenerationAt);

        Assert.DoesNotContain("Internal provider detail", json);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerPaymentReference", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normal_anonymous_and_disabled_administrators_cannot_read_operations_dashboard()
    {
        using var normalUser = factory.CreateClient();
        var normal = await Register(normalUser, "Normal Operations User");
        Assert.Equal(HttpStatusCode.Forbidden, (await normalUser.GetAsync("/api/admin/operations/dashboard")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/operations/dashboard")).StatusCode);

        using var disabledAdmin = factory.CreateClient();
        var disabled = await Register(disabledAdmin, "Disabled Operations Administrator");
        await AddAdminRole(disabled.User.Email);
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(disabled.User.Email);
            Assert.NotNull(user);
            user!.IsActive = false;
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await disabledAdmin.GetAsync("/api/admin/operations/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Auth_session_exposes_only_a_safe_admin_capability_flag_for_page_gating()
    {
        using var client = factory.CreateClient();
        var normal = await Register(client, "Page Gate Customer");
        Assert.False(normal.User.IsAdmin);
        await AddAdminRole(normal.User.Email);

        var updated = await client.GetFromJsonAsync<AuthResponse>("/api/auth/me");
        Assert.NotNull(updated);
        Assert.True(updated!.User.IsAdmin);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"admin-operations-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.AddToRoleAsync(user!, AdminPolicies.Role)).Succeeded);
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
