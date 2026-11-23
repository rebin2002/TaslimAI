using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class HealthCareReportAuthorizationApiFactory : TaslimApiFactory
{
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), $"taslim-health-care-test-{Guid.NewGuid():N}");

    public CapturedTestLogs CapturedLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Files:StorageProvider", FileStorageProviders.Local);
        builder.UseSetting("Files:LocalRootPath", fileRoot);
        builder.ConfigureLogging(logging => logging.AddProvider(CapturedLogs));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(fileRoot)) Directory.Delete(fileRoot, recursive: true);
    }
}

public sealed class HealthCareReportAuthorizationTests : IClassFixture<HealthCareReportAuthorizationApiFactory>
{
    private const string SyntheticReportBody =
        "SYNTHETIC TEST FIXTURE ONLY; NOT REAL PATIENT DATA. " +
        "Private health-care report marker: synthetic-report-content-7A91C5.";

    private readonly HealthCareReportAuthorizationApiFactory factory;

    public HealthCareReportAuthorizationTests(HealthCareReportAuthorizationApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Revoking_workspace_membership_denies_synthetic_health_care_report_attachment_read_without_logging_content()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Synthetic Report Owner");
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post,
            $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/projects",
            new { name = "Synthetic Health Care Project", type = "Research" });

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Synthetic Report Member");

        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var representationId = Guid.NewGuid();
        var storageKey = $"health-care-test/{Guid.NewGuid():N}.txt";
        var reportBytes = Encoding.UTF8.GetBytes(SyntheticReportBody);
        var now = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = now,
            });

            var reportFile = new StoredFile
            {
                Id = fileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                ProjectId = project.Id,
                OriginalFileName = "synthetic-health-care-report.txt",
                StoredFileName = "synthetic-health-care-report.txt",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = reportBytes.Length,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = storageKey,
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            };
            db.StoredFiles.Add(reportFile);
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = ownerAuth.User.Id,
                Name = "Synthetic private health-care report",
                AssetType = AssetTypes.Research,
                MimeType = "text/plain",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AssetRepresentations.Add(new AssetRepresentation
            {
                Id = representationId,
                AssetId = assetId,
                StoredFileId = fileId,
                RepresentationType = "txt",
                FileName = "synthetic-health-care-report.txt",
                ContentType = "text/plain",
                SizeBytes = reportBytes.Length,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();

            var storage = services.GetRequiredService<IFileStorageService>();
            await using var content = new MemoryStream(reportBytes, writable: false);
            await storage.StoreAsync(storageKey, content);
        }

        var downloadPath = $"/api/assets/{assetId}/representations/{representationId}/download";
        using (var beforeRevocation = await member.GetAsync(downloadPath))
        {
            Assert.Equal(HttpStatusCode.OK, beforeRevocation.StatusCode);
            Assert.Equal(SyntheticReportBody, await beforeRevocation.Content.ReadAsStringAsync());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var membership = await db.WorkspaceMembers.SingleAsync(item =>
                item.WorkspaceId == ownerAuth.PersonalWorkspace.Id && item.UserId == memberAuth.User.Id);
            db.WorkspaceMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        factory.CapturedLogs.Clear();
        using var afterRevocation = await member.GetAsync(downloadPath);
        var responseBody = await afterRevocation.Content.ReadAsStringAsync();
        var logs = factory.CapturedLogs.Messages;

        Assert.Equal(HttpStatusCode.NotFound, afterRevocation.StatusCode);
        Assert.DoesNotContain(SyntheticReportBody, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-report-content-7A91C5", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticReportBody, logs, StringComparison.Ordinal);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"health-care-report-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}

public sealed class CapturedTestLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> messages = new();

    public string Messages => string.Join(Environment.NewLine, messages.ToArray());

    public ILogger CreateLogger(string categoryName) => new CapturedTestLogger(categoryName, messages);

    public void Clear()
    {
        while (messages.TryDequeue(out _)) { }
    }

    public void Dispose() { }

    private sealed class CapturedTestLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue($"{categoryName} {logLevel}: {formatter(state, exception)}");
            if (exception is not null) messages.Enqueue(exception.ToString());
        }
    }
}
