using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

namespace Taslim.Api.Tests;

/// <summary>
/// Covers incident #459's readback half: a Chat attachment stored in an
/// S3-compatible bucket such as Cloudflare R2 can fail, stall, or return a
/// truncated body while the object is streaming, after the storage adapter has
/// already returned a readable stream. These tests use synthetic fixtures and
/// synthetic credential text only; no real provider, bucket, credential, or
/// patient data is contacted.
/// </summary>
public sealed class ChatAttachmentStorageReadbackTests
{
    private const string LeakMarker = "synthetic-r2-endpoint-account-and-object-key-material";

    [Fact]
    public async Task Mid_stream_provider_fault_keeps_attachment_contract_instead_of_generation_failure()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.ProviderFaultDuringBodyRead);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Provider Fault Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;

        var (status, code, body) = await SendMessage(client, conversation, file.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);
        Assert.DoesNotContain(LeakMarker, body, StringComparison.Ordinal);
        Assert.Equal(callsBefore, recorder.CallCount);
    }

    [Fact]
    public async Task Mid_stream_transport_fault_keeps_attachment_contract_instead_of_generation_failure()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.TransportFaultDuringBodyRead);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Transport Fault Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;

        var (status, code, body) = await SendMessage(client, conversation, file.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);
        Assert.DoesNotContain(LeakMarker, body, StringComparison.Ordinal);
        Assert.Equal(callsBefore, recorder.CallCount);
    }

    [Fact]
    public async Task Truncated_object_body_is_rejected_before_it_reaches_the_provider()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.TruncatedBody);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Truncated Body Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;

        var (status, code, _) = await SendMessage(client, conversation, file.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);
        Assert.Equal(callsBefore, recorder.CallCount);
    }

    [Fact]
    public async Task Attachment_readback_fault_records_attachment_usage_code_not_generation_failure()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.ProviderFaultDuringBodyRead);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Code Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);

        var (status, code, _) = await SendMessage(client, conversation, file.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);

        // The accounting trail must name the storage readback, not the model
        // provider, otherwise incident triage charges a storage fault to AI
        // generation.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var transaction = Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.ConversationId == conversation.Id)
            .ToListAsync());
        Assert.Equal(UsageTransactionStatus.Failed, transaction.Status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", transaction.FailureCode);
    }

    [Fact]
    public async Task Stalled_object_body_fails_closed_within_the_storage_operation_timeout()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.StalledBody);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Stalled Body Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);

        var started = DateTime.UtcNow;
        var (status, code, _) = await SendMessage(client, conversation, file.Id);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);
        Assert.True(factory.Storage.ReadbackCancellationObserved, "the stalled read must be cancelled");
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"the readback must not wait for the client, waited {elapsed}");
    }

    [Fact]
    public async Task Provider_fault_text_is_never_logged_or_returned_during_attachment_readback()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.ProviderFaultDuringBodyRead);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Sanitized Logging Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);

        var (status, code, body) = await SendMessage(client, conversation, file.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", code);

        // The fault is recorded, but only as a sanitized category.
        Assert.Contains(factory.LogEntries, entry => entry.Contains("Chat attachment readback failed", StringComparison.Ordinal));
        Assert.Contains(factory.LogEntries, entry => entry.Contains("FailureClass=providerresponse", StringComparison.Ordinal));

        // No captured log line or response body may carry provider message text,
        // which is where endpoint account identifiers and object keys appear.
        Assert.DoesNotContain(factory.LogEntries, entry => entry.Contains(LeakMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(LeakMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthy_attachment_readback_still_supplies_the_vision_data_url()
    {
        using var factory = new ReadbackFaultFactory(ReadbackFault.None);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await Register(client, "Healthy Reader");
        var file = await UploadImage(client, auth, "scan.png");
        var conversation = await CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();

        var (status, code, _) = await SendMessage(client, conversation, file.Id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(code);
        var attachment = Assert.Single(recorder.LastRequest!.Attachments!);
        Assert.StartsWith("data:image/png;base64,", attachment.DataUrl, StringComparison.Ordinal);
        Assert.Equal(file.SizeBytes, Convert.FromBase64String(attachment.DataUrl!.Split(',', 2)[1]).LongLength);
    }

    [Fact]
    public void Readback_classification_keeps_cancellation_and_unexpected_defects_visible()
    {
        Assert.False(StorageReadbackFaults.IsReadbackFault(new OperationCanceledException()));
        Assert.False(StorageReadbackFaults.IsReadbackFault(new InvalidOperationException("internal defect")));
        Assert.Equal(StorageReadbackFaults.FailureCategory.Unexpected, StorageReadbackFaults.Classify(new InvalidOperationException("internal defect")));

        Assert.True(StorageReadbackFaults.IsReadbackFault(new AmazonS3Exception(LeakMarker) { StatusCode = HttpStatusCode.InternalServerError }));
        Assert.True(StorageReadbackFaults.IsReadbackFault(new IOException("connection was closed")));
        Assert.True(StorageReadbackFaults.IsReadbackFault(new FileStorageOperationException()));

        // The SDK can wrap the real transport failure, so the chain is inspected.
        Assert.Equal(
            StorageReadbackFaults.FailureCategory.Transport,
            StorageReadbackFaults.Classify(new InvalidOperationException("wrapper", new IOException("connection was closed"))));
    }

    [Fact]
    public async Task Bounded_read_rejects_empty_and_oversized_bodies()
    {
        await using (var empty = new MemoryStream())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => StorageReadbackFaults.ReadBoundedAsync(empty, 1024, CancellationToken.None));
        }

        await using (var oversized = new MemoryStream(new byte[8 * 1024]))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => StorageReadbackFaults.ReadBoundedAsync(oversized, 1024, CancellationToken.None));
        }

        Assert.Equal(StorageReadbackFaults.FailureCategory.InvalidContent, StorageReadbackFaults.Classify(new InvalidDataException("oversized")));
    }

    internal enum ReadbackFault
    {
        None,
        ProviderFaultDuringBodyRead,
        TransportFaultDuringBodyRead,
        TruncatedBody,
        StalledBody,
    }

    /// <summary>
    /// Synthetic stand-in for an S3-compatible object store. Writes always
    /// succeed so the incident's readback phase can be isolated; reads behave
    /// according to the selected fault mode.
    /// </summary>
    internal sealed class ReadbackStorageService(ReadbackFault fault) : IFileStorageService
    {
        private readonly ConcurrentDictionary<string, byte[]> objects = new();
        public bool ReadbackCancellationObserved { get; private set; }
        public string ProviderKey => FileStorageProviders.S3Compatible;

        public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[storageKey] = buffer.ToArray();
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            var content = objects.TryGetValue(storageKey, out var stored) ? stored : [];
            Stream stream = fault switch
            {
                ReadbackFault.ProviderFaultDuringBodyRead => new FaultingReadStream(
                    new AmazonS3Exception($"Error making out request to {LeakMarker}") { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" },
                    content),
                ReadbackFault.TransportFaultDuringBodyRead => new FaultingReadStream(new IOException($"Unable to read data from the transport connection: {LeakMarker}"), content),
                ReadbackFault.TruncatedBody => new MemoryStream(content[..Math.Max(0, content.Length / 2)]),
                ReadbackFault.StalledBody => new StalledReadStream(this, content),
                _ => new MemoryStream(content, writable: false),
            };
            return Task.FromResult<Stream?>(stream);
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            objects.TryRemove(storageKey, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(objects.ContainsKey(storageKey));

        private sealed class FaultingReadStream(Exception fault, byte[] content) : MemoryStream(content, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                throw fault;
            }
        }

        private sealed class StalledReadStream(ReadbackStorageService owner, byte[] content) : MemoryStream(content, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    owner.ReadbackCancellationObserved = true;
                    throw;
                }

                return 0;
            }
        }
    }

    internal sealed class ReadbackFaultFactory : WebApplicationFactory<Program>
    {
        private readonly ReadbackFault fault;
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public ReadbackFaultFactory(ReadbackFault fault) => this.fault = fault;

        public ReadbackStorageService Storage { get; private set; } = null!;
        public ConcurrentBag<string> LogEntries { get; } = [];

        /// <summary>
        /// Each test gets its own in-memory database, so the schema has to be
        /// created before the first request. Data protection keys persist into
        /// the same context, which the antiforgery layer needs to register a
        /// user.
        /// </summary>
        public void EnsureSchema()
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            connection.Open();
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Synthetic, structurally shaped values only. Nothing here is a
                // real endpoint, account, bucket, credential, or key.
                ["Files:StorageProvider"] = "S3Compatible",
                ["Files:S3Endpoint"] = "https://synthetic-account.r2.invalid",
                ["Files:S3Region"] = "auto",
                ["Files:S3Bucket"] = "synthetic-private-bucket",
                ["Files:S3AccessKey"] = "synthetic-access-key",
                ["Files:S3SecretKey"] = "synthetic-secret-key",
                ["Files:StorageOperationTimeoutSeconds"] = "1",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddSingleton(connection);
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));

                Storage = new ReadbackStorageService(fault);
                services.RemoveAll<IFileStorageService>();
                services.AddSingleton<IFileStorageService>(Storage);

                services.RemoveAll<IChatCompletionService>();
                services.AddSingleton<RecordingChatCompletionService>();
                services.AddSingleton<IChatCompletionService>(provider => provider.GetRequiredService<RecordingChatCompletionService>());

                services.AddSingleton<ILoggerProvider>(_ => new CollectingLoggerProvider(LogEntries));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) connection.Dispose();
        }

        private sealed class CollectingLoggerProvider(ConcurrentBag<string> entries) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Collector(entries);

            public void Dispose()
            {
            }

            private sealed class Collector(ConcurrentBag<string> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    // Mirrors how real sinks render an entry: formatted message
                    // plus the exception graph when the caller passes it in.
                    entries.Add($"{formatter(state, exception)}|{exception?.ToString()}|{exception?.Message}");
                }
            }
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email = $"readback-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<StoredFileDto> UploadImage(HttpClient client, AuthResponse auth, string fileName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngFixture(128));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/files")
        {
            Content = form,
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Ready", stored.Status);
        return stored;
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, AuthResponse auth)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations")
        {
            Content = JsonContent.Create(new { title = "Medical record follow-up" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<(HttpStatusCode Status, string? Code, string Body)> SendMessage(
        HttpClient client,
        ConversationDto conversation,
        Guid fileId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new
            {
                content = "Summarize the attached scan.",
                requestId = Guid.NewGuid().ToString("N"),
                attachmentIds = new[] { fileId },
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        string? code = null;
        if (!response.IsSuccessStatusCode)
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(body);
            code = payload.GetProperty("error").GetProperty("code").GetString();
        }

        return (response.StatusCode, code, body);
    }

    /// <summary>
    /// Builds a synthetic PNG-shaped payload: the real signature so content
    /// validation passes, then deterministic filler to the requested length.
    /// </summary>
    private static byte[] PngFixture(int length)
    {
        var payload = new byte[Math.Max(length, 16)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(payload, 0);
        for (var index = 8; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        return payload;
    }
}
