using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// API-level regression for production issue #459: a Chat PDF upload attempted through
/// <c>POST /api/workspaces/{workspaceId}/files</c> failed with HTTP 503 after the configured
/// S3-compatible provider rejected the object PUT at the transport level
/// (<see cref="AmazonS3Exception"/> with <c>StatusCode = 0</c>, <c>ErrorCode = null</c>,
/// <c>RequestId = null</c> — that is, no HTTP response was ever received).
/// The existing adapter-level suites (<c>S3CompatibleStorageTests</c> and
/// <c>S3CompatibleStorageDiagnosticsTests</c>) exercise <c>S3CompatibleFileStorageService</c>
/// and its sanitized failure classification directly. This file covers the user-visible HTTP
/// path end to end: stable error classification, no provider-detail leakage into the response or
/// logs, a terminal <c>Failed</c> row with no Ready orphan and an attempted fail-closed object
/// cleanup, and no file access by users outside the owning workspace.
/// No live R2/S3 endpoint, credentials, or provider traffic is involved: the SDK client seam
/// (<see cref="IS3CompatibleObjectClient"/>) throws the recorded incident exception in-process.
/// </summary>
public sealed class WorkspaceFileUploadStorageFailureTests
    : IClassFixture<WorkspaceFileUploadStorageFailureTests.TransportFailingS3Api>
{
    // Synthetic canary values exist only to prove that none of the incident-sensitive details —
    // endpoint, bucket, object key, credentials, request token, or file content — ever surface
    // through the API response or application logs. They are not real resources.
    private const string SyntheticEndpoint = "https://synthetic-r2-endpoint.invalid";
    private const string SyntheticBucket = "synthetic-bucket-leak-canary";
    private const string SyntheticAccessKey = "synthetic-access-key-leak-canary";
    private const string SyntheticSecretKey = "synthetic-secret-key-leak-canary";
    private const string SyntheticPutToken = "synthetic-request-token-leak-canary";
    private const string StableUploadFailureMessage = "We couldn't store this file. Please try again.";

    private readonly TransportFailingS3Api api;

    public WorkspaceFileUploadStorageFailureTests(TransportFailingS3Api api)
    {
        this.api = api;
        using var scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Transport_level_put_failure_returns_stable_503_without_provider_details_or_ready_orphan()
    {
        using var client = api.CreateClient();
        var auth = await RegisterAsync(client, "S3 Transport Failure Owner");

        var response = await UploadPdfAsync(client, auth.PersonalWorkspace.Id);

        // 1. The user-visible classification is stable regardless of transport subtype:
        //    HTTP 503, code FILE_STORAGE_OPERATION_FAILED, and the fixed incident message —
        //    not a file-type validation rejection and not an unhandled 500.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using (var json = JsonDocument.Parse(body))
        {
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("FILE_STORAGE_OPERATION_FAILED", error.GetProperty("code").GetString());
            Assert.Equal(StableUploadFailureMessage, error.GetProperty("message").GetString());
        }

        // 2. The response leaks no SDK type or provider detail.
        AssertNoCanary(body);
        Assert.DoesNotContain("AmazonS3", body, StringComparison.OrdinalIgnoreCase);

        // 3. Persistence reaches a terminal state: exactly one Failed row, never a Ready/
        //    Processing/Uploading orphan, attributed to the configured provider.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var rows = db.StoredFiles
            .Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id)
            .ToList();
        var row = Assert.Single(rows);
        Assert.Equal(StoredFileStatus.Failed, row.Status);
        Assert.Equal(FileExtractionStatus.Failed, row.TextExtractionStatus);
        Assert.Equal(FileStorageProviders.S3Compatible, row.StorageProvider);
        Assert.DoesNotContain(
            rows,
            file => file.Status is StoredFileStatus.Ready
                or StoredFileStatus.Processing
                or StoredFileStatus.Uploading);

        // 4. Fail-closed object hygiene: the PUT was attempted once, nothing was durably
        //    stored, and cleanup of any partial object was attempted for the failed upload.
        Assert.Contains(row.StorageKey, api.ObjectClient.PutAttemptedKeys);
        Assert.DoesNotContain(row.StorageKey, api.ObjectClient.StoredKeys);
        Assert.Contains(row.StorageKey, api.ObjectClient.DeleteAttemptedKeys);

        // 5. Diagnostics are sanitized: a structured status-0 put failure with a failure class
        //    is logged, but no canary (endpoint, bucket, key, credentials, token) and no raw
        //    transport exception text appear in any log line. The failure-class value itself is
        //    intentionally not pinned here; open diagnostics work (#463/#680) owns that wording.
        var lines = api.CapturedLogs.Lines;
        Assert.Contains(
            lines,
            line => line.Contains("Operation=put", StringComparison.Ordinal)
                && line.Contains("StatusCode=0", StringComparison.Ordinal)
                && line.Contains("FailureClass=", StringComparison.Ordinal));
        AssertNoCanary(string.Join("\n", lines));
    }

    [Fact]
    public async Task Failed_upload_row_is_not_accessible_outside_its_owning_workspace()
    {
        using var owner = api.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "S3 Transport Failure Isolation Owner");

        var upload = await UploadPdfAsync(owner, ownerAuth.PersonalWorkspace.Id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);

        Guid fileId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = Assert.Single(db.StoredFiles.Where(file =>
                file.WorkspaceId == ownerAuth.PersonalWorkspace.Id));
            Assert.Equal(StoredFileStatus.Failed, row.Status);
            fileId = row.Id;
        }

        using var stranger = api.CreateClient();
        var strangerAuth = await RegisterAsync(stranger, "S3 Transport Failure Stranger");
        Assert.NotEqual(strangerAuth.PersonalWorkspace.Id, ownerAuth.PersonalWorkspace.Id);

        // A non-member can neither fetch the failed row's metadata nor list the workspace.
        using var fileAccess = await stranger.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.Forbidden, fileAccess.StatusCode);
        using var listAccess = await stranger.GetAsync(
            $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/files");
        Assert.Equal(HttpStatusCode.Forbidden, listAccess.StatusCode);

        // The owner still sees the row as a terminal Failed file with no leaked details.
        using var ownerView = await owner.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, ownerView.StatusCode);
        var ownerBody = await ownerView.Content.ReadAsStringAsync();
        var dto = await ownerView.Content.ReadFromJsonAsync<StoredFileDto>();
        Assert.NotNull(dto);
        Assert.Equal("Failed", dto.Status);
        Assert.Equal("Failed", dto.TextExtractionStatus);
        Assert.Null(dto.ExtractedTextLength);
        AssertNoCanary(ownerBody);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string name)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName = name,
            email = $"upload-s3-failure-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> UploadPdfAsync(HttpClient client, Guid workspaceId)
    {
        // A syntactically valid PDF header so the failure under test is storage transport,
        // not file-type validation (per the #459 evidence that the 503 is not a validation rejection).
        var content = $"%PDF-1.4\nworkspace upload regression marker {Guid.NewGuid():N}\ncontent canary: {SyntheticPutToken}\n%%EOF";
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "chat-upload.pdf");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/workspaces/{workspaceId}/files")
        {
            Content = form,
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static void AssertNoCanary(string text)
    {
        Assert.DoesNotContain(SyntheticEndpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticBucket, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticAccessKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecretKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPutToken, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the real <see cref="S3CompatibleFileStorageService"/> adapter and full upload
    /// pipeline behind the HTTP API while the SDK client seam fails every PUT exactly like
    /// the #459 incident: no HTTP response, so StatusCode=0, ErrorCode=null, RequestId=null.
    /// </summary>
    public sealed class TransportFailingS3Api : TaslimApiFactory
    {
        public TransportFailingS3Api()
        {
            ObjectClient = new TransportFailingObjectClient();
            CapturedLogs = new CapturingLogs();
        }

        internal TransportFailingObjectClient ObjectClient { get; }

        internal CapturingLogs CapturedLogs { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileStorageService>();
                services.AddSingleton<IFileStorageService>(provider =>
                    new S3CompatibleFileStorageService(
                        ObjectClient,
                        Options.Create(new Taslim.Api.Files.FileOptions
                        {
                            StorageProvider = FileStorageProviders.S3Compatible,
                            S3Endpoint = SyntheticEndpoint,
                            S3Bucket = SyntheticBucket,
                            S3Region = "auto",
                            S3AccessKey = SyntheticAccessKey,
                            S3SecretKey = SyntheticSecretKey,
                        }),
                        provider.GetRequiredService<
                            ILogger<S3CompatibleFileStorageService>>()));
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(CapturedLogs));
            });
        }
    }

    internal sealed class TransportFailingObjectClient : IS3CompatibleObjectClient
    {
        public List<string> PutAttemptedKeys { get; } = new();
        public List<string> StoredKeys { get; } = new();
        public List<string> DeleteAttemptedKeys { get; } = new();

        public Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            PutAttemptedKeys.Add(key);
            // The recorded incident signature: a transport-level failure before any HTTP
            // response, so the SDK surfaces StatusCode=0 with null ErrorCode and RequestId.
            // Canary values ride along in the exception text to prove nothing leaks outward.
            var failure = new AmazonS3Exception(
                $"synthetic transport failure bucket={bucket} key={key} endpoint={SyntheticEndpoint} token={SyntheticPutToken}",
                new HttpRequestException($"synthetic connect failure access={SyntheticAccessKey} secret={SyntheticSecretKey}"))
            {
                StatusCode = (HttpStatusCode)0,
            };
            return Task.FromException(failure);
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            if (!StoredKeys.Contains(key))
            {
                throw new AmazonS3Exception("synthetic missing object")
                {
                    StatusCode = HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey",
                };
            }
            return Task.FromResult<Stream?>(Stream.Null);
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            DeleteAttemptedKeys.Add(key);
            StoredKeys.Remove(key);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredKeys.Contains(key));

        public void Dispose()
        {
        }
    }

    internal sealed class CapturingLogs
    {
        private readonly List<string> lines = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (lines) { return lines.ToList(); } }
        }

        public void Add(string line)
        {
            lock (lines) { lines.Add(line); }
        }
    }

    private sealed class CapturingLoggerProvider(CapturingLogs logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(CapturingLogs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            logs.Add(exception is null ? message : $"{message} | exception: {exception}");
        }
    }
}
