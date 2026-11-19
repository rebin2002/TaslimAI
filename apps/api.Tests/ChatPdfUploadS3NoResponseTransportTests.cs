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
/// Provider-free, in-process negative control raised by the release-QA review on PR #1134
/// (review id 5971425637) for production incident #459: the Chat PDF upload half of the
/// incident surfaced as an <see cref="AmazonS3Exception"/> with <c>StatusCode = 0</c>,
/// <c>ErrorCode = null</c> and <c>RequestId = null</c> — a transport failure in which the
/// S3-compatible provider never returned any HTTP response at all — while the user's API
/// call still returned HTTP 503.
///
/// Companion lanes, deliberately not duplicated here:
/// - <c>ChatPdfUploadS3ServiceUnavailableTests</c> (open PR #1134) pins the *provider answered
///   with HTTP 503* class at the Chat boundary. That fake proves the response-backed status;
///   it does not reproduce the recorded incident exception, which had no HTTP response behind it.
/// - <c>WorkspaceFileUploadStorageFailureTests</c> (open PR #768) pins this same no-response
///   class at the upload endpoint only. It asserts nothing about a subsequent Chat turn:
///   no conversation, no usage transaction and no credit-ledger assertions exist there.
/// - <c>WorkspaceFileUploadAmbiguousRetryTests</c> (open PR #797) pins committed-but-
///   unacknowledged PUT retry de-duplication at the upload endpoint.
///
/// What this file adds is exactly the gap the review called out: the *no-response* incident
/// class carried through the real Chat flow, so the billing consequence of the original
/// incident is pinned at the Chat boundary without reworking #768:
/// 1. The upload must fail closed with the same stable 503 contract as the answered-503 lane
///    (the transport class must not degrade into a 500 or a validation rejection), and no
///    successful asset identity may persist (no Ready row, no retained object).
/// 2. The sanitized storage log line must record the incident's diagnostic shape
///    (<c>StatusCode=0</c>, empty <c>ErrorCode</c>/<c>RequestId</c>,
///    <c>FailureClass=transport</c>) while the bucket, endpoint, credentials and PUT token
///    riding inside the exception text never surface in the log or in any HTTP body.
/// 3. A Chat message referencing the failed upload must be refused with 422
///    <c>ATTACHMENT_CONTENT_UNAVAILABLE</c> before any assistant turn starts: zero provider
///    invocations, zero usage transactions and zero credit debits, so a no-response transport
///    outage can never be billed.
/// 4. Exactly one PUT attempt per upload request plus one bounded fail-closed cleanup DELETE;
///    a connection-level failure must not fan out into uncontrolled retries.
/// 5. Recovery must stay available: once the provider answers again the next upload reaches
///    <c>Ready</c> and the same Chat flow succeeds with exactly one usage record.
///
/// Everything runs against an in-process fake S3-compatible client seam
/// (<see cref="IS3CompatibleObjectClient"/>) and an in-memory SQLite database: no live
/// R2/S3 endpoint, no real credentials, no provider traffic, no paid model call,
/// no production or user data.
/// </summary>
public sealed class ChatPdfUploadS3NoResponseTransportTests
{
    private const string SyntheticEndpoint = "https://synthetic-noresponse-account.r2.cloudflarestorage.invalid";
    private const string SyntheticBucket = "synthetic-noresponse-bucket";
    private const string SyntheticAccessKey = "synthetic-noresponse-access-key-do-not-leak";
    private const string SyntheticSecretKey = "synthetic-noresponse-secret-key-do-not-leak";
    private const string SyntheticPutToken = "synthetic-noresponse-put-token-do-not-leak";
    private const string StableUploadFailureMessage = "We couldn't store this file. Please try again.";
    private const string PdfTextMarker = "CHAT_PDF_NORESPONSE_SYNTHETIC_MARKER";

    [Fact]
    public async Task Chat_pdf_upload_no_response_transport_failure_fails_safely_and_is_refused_by_chat_without_any_usage_charge()
    {
        using var api = new NoResponseS3Api();
        api.EnsureDatabase();
        using var client = api.CreateClient();
        var auth = await Register(client, "Chat No-Response Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        // 1. The PDF upload reaches the real upload pipeline and the real S3 adapter, and
        //    the fake provider reproduces the incident's no-response class: the connection
        //    dies with StatusCode 0, ErrorCode null and RequestId null — no HTTP response.
        api.ObjectClient.BeginRequest();
        var upload = await UploadPdf(client, auth.PersonalWorkspace.Id);
        var uploadBody = await upload.Content.ReadAsStringAsync();
        var uploadError = JsonSerializer.Deserialize<JsonElement>(uploadBody);

        // The transport class must fail closed with the exact same stable 503 contract the
        // answered-503 lane pins — not a 500, not a validation rejection.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        Assert.Equal("FILE_STORAGE_OPERATION_FAILED", uploadError.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(StableUploadFailureMessage, uploadError.GetProperty("error").GetProperty("message").GetString());

        // No SDK or transport detail may ride out to the user for the no-response class.
        Assert.DoesNotContain("AmazonS3", uploadBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpRequestException", uploadBody, StringComparison.OrdinalIgnoreCase);

        // 2. Exactly one PUT attempt and one fail-closed cleanup DELETE for this one upload
        //    request: a connection-level failure must not be retried uncontrolled.
        var attempts = api.ObjectClient.DrainOperations();
        Assert.Equal(["put", "delete"], attempts);

        // 3. Nothing was ever stored: no object in the fake bucket, and the row is a
        //    terminal Failed identity, never Ready for any consumer.
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

        // 4. The sanitized log line records the incident's diagnostic shape — status 0,
        //    empty ErrorCode/RequestId, transport failure class — so the no-response class
        //    stays distinguishable from an answered 503 for incident response.
        var putLogs = api.CapturedLogs.Lines.Where(line =>
            line.Contains("S3-compatible storage operation failed", StringComparison.Ordinal) &&
            line.Contains("Operation=put", StringComparison.Ordinal)).ToList();
        Assert.Single(putLogs);
        var putLog = putLogs[0];
        Assert.Contains("StatusCode=0", putLog, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=(null)", putLog, StringComparison.Ordinal);
        Assert.Contains("RequestId=(null)", putLog, StringComparison.Ordinal);
        Assert.Contains("ErrorType=AmazonS3Exception", putLog, StringComparison.Ordinal);
        Assert.Contains("FailureClass=transport", putLog, StringComparison.Ordinal);
        // The diagnostic fields above are the only provider detail allowed into the log:
        // the exception *message* carrying bucket, endpoint, credentials and PUT token must
        // stay out, which the canary sweep over every captured line proves below.

        // 5. The canaries inside the exception text never surface: not in the upload
        //    response, not in any captured log line.
        AssertNoCanaries(uploadBody);
        foreach (var line in api.CapturedLogs.Lines)
        {
            AssertNoCanaries(line);
        }

        // 6. The user follows the retry-style advice in Chat and selects the file the failed
        //    upload produced. Chat must refuse it before any assistant turn begins.
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

        // No assistant turn was attempted for a file the provider never acknowledged.
        Assert.Equal(0, api.ChatCompletion.CallCount);
        Assert.Null(api.ChatCompletion.LastRequest);

        // 7. Nothing may be billed for a no-response transport outage: not a credit debit,
        //    not even a pending usage transaction, not an attachment binding, and the
        //    refused message never became a persisted user turn.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.ChatMessageAttachments.CountAsync(binding => binding.StoredFileId == failedFileId));
            Assert.Equal(0, await db.UsageTransactions.CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
            Assert.Equal(0, await db.UsageTransactions.CountAsync(usage => usage.ConversationId == conversation.Id));
            Assert.Equal(0, await db.CreditLedgerEntries.CountAsync(entry =>
                entry.WorkspaceId == auth.PersonalWorkspace.Id && entry.Type == CreditLedgerEntryType.Debit));

            var userMessages = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id && message.Role == ChatMessageRole.User)
                .ToListAsync();
            Assert.Empty(userMessages);
        }

        AssertNoCanaries(sendBody);
        Assert.DoesNotContain(failedStorageKey, sendBody, StringComparison.Ordinal);

        // 8. Recovery stays available over the same pipeline once the provider answers
        //    again, and the outage row is never reused as a Ready identity.
        api.ObjectClient.FailWithNoResponse = false;
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

        // The healthy turn is the only usage record for the workspace: the refused
        // no-response turn contributed none.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(1, await db.UsageTransactions.CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
        }
    }

    private static void AssertNoCanaries(string payload)
    {
        Assert.DoesNotContain(SyntheticBucket, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticEndpoint, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticAccessKey, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecretKey, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPutToken, payload, StringComparison.Ordinal);
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
            email = $"chat-noresponse-{Guid.NewGuid():N}@example.com",
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
    /// adapter with the SDK client seam replaced by a fake S3-compatible bucket whose PUT dies
    /// the way #459 recorded it: no HTTP response from the provider at all
    /// (<c>StatusCode = 0</c>, <c>ErrorCode = null</c>, <c>RequestId = null</c>).
    /// </summary>
    private sealed class NoResponseS3Api : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public NoResponseS3Api()
        {
            connection.Open();
            ObjectClient = new NoResponseObjectClient();
            CapturedLogs = new CapturingLogs();
            ChatCompletion = new CountingChatCompletionService();
        }

        internal NoResponseObjectClient ObjectClient { get; }
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
    /// test can prove bounded, single-attempt behaviour, and kills the PUT with the exact
    /// no-response exception class #459 recorded — a transport abort behind the SDK wrapper
    /// with no HTTP status, no S3 error code and no provider request id. The synthetic
    /// credential canaries ride only in the exception text, never in the SDK fields.
    /// </summary>
    internal sealed class NoResponseObjectClient : IS3CompatibleObjectClient
    {
        private readonly ConcurrentQueue<string> operations = new();
        private readonly ConcurrentDictionary<string, byte[]> objects = new();

        public bool FailWithNoResponse { get; set; } = true;
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

            if (FailWithNoResponse)
            {
                // The incident as recorded: the provider never returned an HTTP response.
                // StatusCode stays 0 and ErrorCode/RequestId stay null — that null shape is
                // what distinguishes this class from #1134's answered-503 fake.
                var failure = new AmazonS3Exception(
                    $"synthetic transport abort bucket={bucket} key={key} endpoint={SyntheticEndpoint} token={SyntheticPutToken}",
                    new HttpRequestException($"synthetic connection reset access={SyntheticAccessKey} secret={SyntheticSecretKey}"))
                {
                    StatusCode = (HttpStatusCode)0,
                };
                Assert.Null(failure.ErrorCode);
                Assert.Null(failure.RequestId);
                return Task.FromException(failure);
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
            yield return new AiMessageCompleted(new AiUsageMetadata("mock", "taslim-test-noresponse", null, null, null, 0m, 0m, 0, "test-complete", true));
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new AiGenerationResult("recorded", new AiUsageMetadata("mock", "taslim-test-noresponse", null, null, null, 0m, 0m, 0, "test-complete", true)));
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
