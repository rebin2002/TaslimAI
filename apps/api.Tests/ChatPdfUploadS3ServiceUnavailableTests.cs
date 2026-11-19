using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, in-process regression for the user-facing Chat PDF upload half of
/// production incident #459, pinned at the S3-compatible PUT boundary with the exact
/// status class the incident surfaced (HTTP 503 Service Unavailable).
///
/// Companion lanes, deliberately not reworked here:
/// - <c>S3CompatibleStorageTests.Provider_failures_become_safe_storage_errors_without_secret_text</c>
///   (on <c>main</c>) pins the adapter's 403/AccessDenied classification only.
/// - <c>WorkspaceFileUploadStorageFailureTests</c> (open PR #768) pins the ambiguous
///   transport case (<c>StatusCode = 0</c>, no HTTP response at all) at the upload endpoint.
/// - <c>WorkspaceFileUploadAmbiguousRetryTests</c> (open PR #797) pins committed-but-
///   unacknowledged PUT retry de-duplication at the upload endpoint.
/// - PR #680 owns PUT request construction / stream length and R2 diagnostics; PR #857
///   owns attachment readback; PR #911 owns PDF parser extraction. None of those lanes is
///   touched here.
///
/// What this file adds is the boundary those lanes leave open: what the *Chat* user sees
/// after a 503 PUT, and what the platform must not do as a consequence.
/// 1. The upload must fail safely with the actionable incident message and must never
///    persist a successful asset identity (no Ready row, no Ready DTO, no object retained).
/// 2. A Chat message that references the failed upload must be refused before any
///    assistant turn starts, must persist no attachment binding, and must create no usage
///    transaction at all, so a storage outage can never be billed.
/// 3. Recovery must remain possible: the user's next upload must succeed through the same
///    pipeline.
/// 4. The PUT must be bounded to exactly one provider attempt per upload request and the
///    fail-closed cleanup to exactly one DELETE, so a 503 cannot fan out into uncontrolled
///    retries against an unhealthy provider.
/// 5. The synthetic bucket, endpoint and credentials carried by the failing client must
///    never surface in the upload response, the storage log line, the Chat response or the
///    Chat-side log line.
///
/// Everything runs against an in-process fake S3-compatible client seam
/// (<see cref="IS3CompatibleObjectClient"/>) and an in-memory SQLite database: no live
/// R2/S3 endpoint, no real credentials, no provider traffic, no paid model call,
/// no production or user data.
/// </summary>
public sealed class ChatPdfUploadS3ServiceUnavailableTests
{
    private const string SyntheticEndpoint = "https://synthetic-account.r2.cloudflarestorage.com";
    private const string SyntheticBucket = "synthetic-private-bucket";
    private const string SyntheticAccessKey = "synthetic-access-key-do-not-leak";
    private const string SyntheticSecretKey = "synthetic-secret-key-do-not-leak";
    private const string SyntheticPutToken = "synthetic-put-token-do-not-leak";
    private const string StableUploadFailureMessage = "We couldn't store this file. Please try again.";
    private const string PdfTextMarker = "CHAT_PDF_503_SYNTHETIC_MARKER";

    [Fact]
    public async Task Chat_pdf_upload_503_fails_safely_and_is_refused_by_chat_without_any_usage_charge()
    {
        using var api = new ServiceUnavailableS3Api();
        api.EnsureDatabase();
        using var client = api.CreateClient();
        var auth = await Register(client, "Chat 503 Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        // 1. The PDF upload reaches the real upload pipeline and the real S3 adapter, and
        //    the fake provider answers with the incident's 503 class.
        api.ObjectClient.BeginRequest();
        var upload = await UploadPdf(client, auth.PersonalWorkspace.Id);
        var uploadBody = await upload.Content.ReadAsStringAsync();
        var uploadError = JsonSerializer.Deserialize<JsonElement>(uploadBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        Assert.Equal("FILE_STORAGE_OPERATION_FAILED", uploadError.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(StableUploadFailureMessage, uploadError.GetProperty("error").GetProperty("message").GetString());

        // 2. Exactly one PUT attempt for this one upload request: a 503 must not be
        //    retried in an uncontrolled way against the failing provider.
        var attempts = api.ObjectClient.DrainOperations();
        Assert.Equal(["put", "delete"], attempts);

        // 3. No successful asset identity may survive the failed PUT: the object is not
        //    retained and the row is not Ready for any consumer.
        Assert.Empty(api.ObjectClient.StoredKeys);
        Guid failedFileId;
        StoredFileStatus failedStatus;
        string failedStorageKey;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = Assert.Single(await db.StoredFiles.AsNoTracking().Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id).ToListAsync());
            failedFileId = row.Id;
            failedStatus = row.Status;
            failedStorageKey = row.StorageKey;
            Assert.Equal(StoredFileStatus.Failed, failedStatus);
            Assert.Equal(FileExtractionStatus.Failed, row.TextExtractionStatus);
            Assert.NotNull(row.ProcessedAt);
            Assert.True(string.IsNullOrEmpty(row.ExtractedText));
        }

        Assert.DoesNotContain(SyntheticBucket, uploadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticEndpoint, uploadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticAccessKey, uploadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecretKey, uploadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPutToken, uploadBody, StringComparison.Ordinal);
        foreach (var line in api.CapturedLogs.Lines)
        {
            Assert.DoesNotContain(SyntheticBucket, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticEndpoint, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticAccessKey, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSecretKey, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPutToken, line, StringComparison.Ordinal);
        }

        // 4. The user follows the retry-style advice in Chat and selects the file that the
        //    failed upload produced. Chat must refuse it before any assistant turn begins.
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Summarise the attached brief.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { failedFileId },
        });
        var sendBody = await send.Content.ReadAsStringAsync();
        var sendError = JsonSerializer.Deserialize<JsonElement>(sendBody);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, send.StatusCode);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", sendError.GetProperty("error").GetProperty("code").GetString());

        // No assistant turn was attempted for a file the provider never stored.
        Assert.Equal(0, api.ChatCompletion.CallCount);
        Assert.Null(api.ChatCompletion.LastRequest);

        // 5. Nothing may be charged for a storage outage: not a credit debit, and not even
        //    a pending usage transaction for the refused Chat turn.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.ChatMessageAttachments.CountAsync(binding => binding.StoredFileId == failedFileId));
            Assert.Equal(0, await db.UsageTransactions.CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
            Assert.Equal(0, await db.UsageTransactions.CountAsync(usage => usage.ConversationId == conversation.Id));
            Assert.Equal(0, await db.CreditLedgerEntries.CountAsync(entry =>
                entry.WorkspaceId == auth.PersonalWorkspace.Id && entry.Type == CreditLedgerEntryType.Debit));

            // The refused message never became a completed user turn either.
            var userMessages = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id && message.Role == ChatMessageRole.User)
                .ToListAsync();
            Assert.Empty(userMessages);
        }

        Assert.DoesNotContain(SyntheticBucket, sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticEndpoint, sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticAccessKey, sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecretKey, sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain(failedStorageKey, sendBody, StringComparison.Ordinal);

        // 6. Recovery stays available to the user over the same pipeline once the provider
        //    answers again, and the outage row is never reused as a Ready identity.
        api.ObjectClient.FailWithServiceUnavailable = false;
        var retry = await UploadPdf(client, auth.PersonalWorkspace.Id);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var retried = (await retry.Content.ReadFromJsonAsync<StoredFileDto>())!;

        Assert.NotEqual(failedFileId, retried.Id);
        Assert.Equal("Ready", retried.Status);
        Assert.Equal("Ready", retried.TextExtractionStatus);
        Assert.Equal(StoredFileStatus.Failed, failedStatus);

        var afterRetry = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Summarise the attached brief.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { retried.Id },
        });
        var afterRetryBody = await afterRetry.Content.ReadAsStringAsync();

        Assert.True(afterRetry.IsSuccessStatusCode, afterRetryBody);
        Assert.Equal(1, api.ChatCompletion.CallCount);
        Assert.Contains(PdfTextMarker, api.ChatCompletion.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticBucket, afterRetryBody, StringComparison.Ordinal);

        // The healthy turn is the only usage record for the workspace: the 503 turn
        // contributed none.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(1, await db.UsageTransactions.CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
        }
    }

    private static async Task<HttpResponseMessage> UploadPdf(HttpClient client, Guid workspaceId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(CreatePdf(PdfTextMarker));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "quarterly-brief.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { projectId = (Guid?)null });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-503-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = payload is null ? null : JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Runs the real upload pipeline and the real <see cref="S3CompatibleFileStorageService"/>
    /// adapter with the SDK client seam replaced by a fake S3-compatible bucket that answers
    /// every PUT with HTTP 503 Service Unavailable, matching the incident's S3 response class.
    /// </summary>
    private sealed class ServiceUnavailableS3Api : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public ServiceUnavailableS3Api()
        {
            connection.Open();
            ObjectClient = new RefusingObjectClient();
            CapturedLogs = new CapturingLogs();
            ChatCompletion = new CountingChatCompletionService();
        }

        internal RefusingObjectClient ObjectClient { get; }
        internal CapturingLogs CapturedLogs { get; }
        internal CountingChatCompletionService ChatCompletion { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:StorageProvider"] = FileStorageProviders.S3Compatible,
                ["Files:S3Endpoint"] = SyntheticEndpoint,
                ["Files:S3Region"] = "auto",
                ["Files:S3Bucket"] = SyntheticBucket,
                ["Files:S3AccessKey"] = SyntheticAccessKey,
                ["Files:S3SecretKey"] = SyntheticSecretKey,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddSingleton(connection);
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<IFileStorageService>();
                services.AddSingleton<IFileStorageService>(provider => new S3CompatibleFileStorageService(
                    ObjectClient,
                    Microsoft.Extensions.Options.Options.Create(new FileOptions
                    {
                        StorageProvider = FileStorageProviders.S3Compatible,
                        S3Endpoint = SyntheticEndpoint,
                        S3Bucket = SyntheticBucket,
                        S3Region = "auto",
                        S3AccessKey = SyntheticAccessKey,
                        S3SecretKey = SyntheticSecretKey,
                    }),
                    provider.GetRequiredService<ILogger<S3CompatibleFileStorageService>>()));
                services.RemoveAll<IChatCompletionService>();
                services.AddSingleton<IChatCompletionService>(ChatCompletion);
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(CapturedLogs));
            });
        }

        internal void EnsureDatabase()
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) connection.Dispose();
        }
    }

    /// <summary>
    /// Fake S3-compatible object client: records every provider operation in order so the
    /// test can prove bounded, single-attempt behaviour, and refuses PUTs with a synthetic
    /// 503 whose text carries credential canaries.
    /// </summary>
    internal sealed class RefusingObjectClient : IS3CompatibleObjectClient
    {
        private readonly ConcurrentQueue<string> operations = new();
        private readonly ConcurrentDictionary<string, byte[]> objects = new();

        public bool FailWithServiceUnavailable { get; set; } = true;
        public ICollection<string> StoredKeys => objects.Keys;

        public void BeginRequest()
        {
            while (!operations.IsEmpty) operations.TryDequeue(out _);
        }

        public IReadOnlyList<string> DrainOperations()
        {
            var seen = new List<string>();
            while (!operations.IsEmpty && operations.TryDequeue(out var operation)) seen.Add(operation);
            return seen;
        }

        public Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            operations.Enqueue("put");
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);

            if (FailWithServiceUnavailable)
            {
                // The incident's status class: the provider answered, with 503. Canary
                // values ride in the exception text so the test can prove nothing leaks.
                throw new AmazonS3Exception(
                    $"synthetic provider unavailable bucket={bucket} key={key} endpoint={SyntheticEndpoint} token={SyntheticPutToken}",
                    new HttpRequestException($"synthetic provider body access={SyntheticAccessKey} secret={SyntheticSecretKey}"))
                {
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                    ErrorCode = "ServiceUnavailable",
                    RequestId = "synthetic-request-id",
                };
            }

            objects[key] = buffer.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            operations.Enqueue("get");
            if (!objects.TryGetValue(key, out var content))
            {
                throw new AmazonS3Exception("synthetic missing object")
                {
                    StatusCode = HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey",
                };
            }

            return Task.FromResult<Stream?>(new MemoryStream(content, writable: false));
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            operations.Enqueue("delete");
            objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            operations.Enqueue("head");
            return Task.FromResult(objects.ContainsKey(key));
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingChatCompletionService : IChatCompletionService
    {
        public AiChatRequest? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            await Task.Yield();
            yield return new AiMessageDelta("recorded");
            yield return new AiMessageCompleted(new AiUsageMetadata("mock", "taslim-test-503", null, null, null, 0m, 0m, 0, "test-complete", true));
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new AiGenerationResult("recorded", new AiUsageMetadata("mock", "taslim-test-503", null, null, null, 0m, 0m, 0, "test-complete", true)));
        }
    }

    private sealed class CapturingLogs
    {
        private readonly List<string> lines = [];

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
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            logs.Add(exception is null ? message : $"{message} | exception: {exception}");
        }
    }

    /// <summary>
    /// Minimal structurally valid single-page PDF whose text layer carries the marker, so a
    /// healthy upload reaches <c>Ready</c> through the real extractor.
    /// </summary>
    private static byte[] CreatePdf(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var streamContent = $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {streamContent.Length} >>\nstream\n{streamContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        foreach (var (value, index) in objects.Select((value, index) => (value, index)))
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{index + 1} 0 obj\n{value}\nendobj\n");
        }

        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void WriteAscii(Stream output, string value) => output.Write(Encoding.ASCII.GetBytes(value));
}
