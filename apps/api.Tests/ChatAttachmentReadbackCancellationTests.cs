using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
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
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public enum ReadbackCancellationStyle
{
    /// <summary>The AWS SDK client layer wraps the cancellation in AmazonClientException.</summary>
    ClientWrapped,
    /// <summary>The HTTP transport layer wraps the cancellation in IOException.</summary>
    TransportWrapped,
}

/// <summary>
/// Independent-review reproduction for the wrapped-cancellation defect on the Chat attachment
/// readback path. An S3-compatible SDK does not always surface a caller abort as a raw
/// <see cref="OperationCanceledException"/>: the client and transport layers report it as an
/// <see cref="AmazonClientException"/> or an <see cref="IOException"/> with the cancellation as
/// the inner exception. <see cref="StorageReadbackFaults.Classify"/> returned on the first
/// recognized OUTER exception, so such a cancellation was classified as a storage fault and
/// ChatController re-mapped it to FileContentUnavailable even though the caller token had
/// fired. End state: the usage ledger recorded Failed/ATTACHMENT_CONTENT_UNAVAILABLE (and a
/// spurious cost-control anomaly) instead of Cancelled/GENERATION_CANCELLED for a request the
/// user cancelled. Synthetic fixtures only; no real provider, bucket, credential, or patient
/// data is contacted.
/// </summary>
public sealed class ChatAttachmentReadbackCancellationTests
{
    internal const string LeakMarker = "synthetic-r2-endpoint-account-and-object-key-material";

    [Theory]
    [InlineData(ReadbackCancellationStyle.ClientWrapped)]
    [InlineData(ReadbackCancellationStyle.TransportWrapped)]
    public async Task Caller_cancellation_wrapped_by_the_sdk_finalizes_the_ledger_as_cancelled_not_failed(ReadbackCancellationStyle style)
    {
        using var factory = new ReadbackCancellationFactory(style, storageOperationTimeoutSeconds: 60);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await ReadbackCancellationFixtures.Register(client);
        var file = await ReadbackCancellationFixtures.UploadImage(client, auth, "scan.png");
        var conversation = await ReadbackCancellationFixtures.CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;

        using var caller = new CancellationTokenSource();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new
            {
                content = "Summarize the attached scan.",
                requestId = Guid.NewGuid().ToString("N"),
                attachmentIds = new[] { file.Id },
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var sendTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, caller.Token);

        // Wait until the server is genuinely inside the streamed body read, then abort the caller.
        using (var gate = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await factory.Storage.ReadEntered.Task.WaitAsync(gate.Token);
        }

        caller.Cancel();

        // Poll for the server-side finalization of the usage transaction.
        UsageTransaction? final = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            final = await db.UsageTransactions.AsNoTracking()
                .FirstOrDefaultAsync(item => item.ConversationId == conversation.Id);
            if (final is { Status: UsageTransactionStatus.Cancelled or UsageTransactionStatus.Failed or UsageTransactionStatus.Completed })
                break;
            await Task.Delay(100);
        }

        try { await sendTask; }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }

        Assert.NotNull(final);
        // Defect expectation: before the fix this records Failed/ATTACHMENT_CONTENT_UNAVAILABLE.
        Assert.Equal(UsageTransactionStatus.Cancelled, final!.Status);
        Assert.Equal("GENERATION_CANCELLED", final.FailureCode);
        // The cancellation must never reach the model provider, and no provider wrapper text
        // (endpoint, account, object key material) may appear in any captured log line.
        Assert.Equal(callsBefore, recorder.CallCount);
        Assert.DoesNotContain(LeakMarker, string.Join("\n", factory.LogEntries), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReadbackCancellationStyle.ClientWrapped)]
    [InlineData(ReadbackCancellationStyle.TransportWrapped)]
    public async Task Readback_timeout_wrapped_by_the_sdk_still_keeps_the_content_unavailable_contract(ReadbackCancellationStyle style)
    {
        // The caller never cancels here: the linked readback timeout fires and the
        // SDK surfaces it wrapped. Cancellation precedence must not let this read
        // as a user cancellation; it stays a storage fault on the attachment
        // contract with the sanitized timeout class in the log.
        using var factory = new ReadbackCancellationFactory(style, storageOperationTimeoutSeconds: 1);
        factory.EnsureSchema();
        using var client = factory.CreateClient();
        var auth = await ReadbackCancellationFixtures.Register(client);
        var file = await ReadbackCancellationFixtures.UploadImage(client, auth, "scan.png");
        var conversation = await ReadbackCancellationFixtures.CreateConversation(client, auth);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();

        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new
            {
                content = "Summarize the attached scan.",
                requestId = Guid.NewGuid().ToString("N"),
                attachmentIds = new[] { file.Id },
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ATTACHMENT_CONTENT_UNAVAILABLE", body, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CallCount);
        var logs = string.Join("\n", factory.LogEntries);
        Assert.Contains("FailureClass=timeout", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakMarker, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakMarker, body, StringComparison.Ordinal);
    }
}

internal sealed class ReadbackCancellationFactory : WebApplicationFactory<Program>
{
    private readonly ReadbackCancellationStyle style;
    private readonly int storageOperationTimeoutSeconds;
    // Keep the shared in-memory database alive while each request or polling scope opens its own connection.
    private readonly SqliteConnection connection = new($"Data Source=readback-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");

    internal ReadbackCancellationFactory(ReadbackCancellationStyle style, int storageOperationTimeoutSeconds)
    {
        this.style = style;
        this.storageOperationTimeoutSeconds = storageOperationTimeoutSeconds;
    }

    internal ReadbackCancellationStorageService Storage { get; private set; } = null!;
    internal ConcurrentBag<string> LogEntries { get; } = [];

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
            // Synthetic, structurally shaped values only. Nothing here is a real endpoint,
            // account, bucket, credential, or key.
            // The recording chat service accepts the image read back by this fixture.
            ["Ai:Models:mock:ProviderKey"] = "mock",
            ["Ai:Models:mock:SupportsVision"] = "true",
            ["Files:StorageProvider"] = "S3Compatible",
            ["Files:S3Endpoint"] = "https://synthetic-account.r2.invalid",
            ["Files:S3Region"] = "auto",
            ["Files:S3Bucket"] = "synthetic-private-bucket",
            ["Files:S3AccessKey"] = "synthetic-access-key",
            ["Files:S3SecretKey"] = "synthetic-secret-key",
            ["Files:StorageOperationTimeoutSeconds"] = storageOperationTimeoutSeconds.ToString(),
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection.ConnectionString));

            Storage = new ReadbackCancellationStorageService(style);
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
        public void Dispose() { }

        private sealed class Collector(ConcurrentBag<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // Mirrors how real sinks render an entry: formatted message plus the full
                // exception graph, which is where provider wrapper text would leak.
                entries.Add($"{formatter(state, exception)}|{exception?.ToString()}|{exception?.Message}");
            }
        }
    }
}

internal sealed class ReadbackCancellationStorageService(ReadbackCancellationStyle style) : IFileStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new();
    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Stream stream = new CancellationWrappingStream(this, content, style);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(objects.ContainsKey(storageKey));

    /// <summary>
    /// Realistic SDK-style behavior: the body read blocks until the caller aborts, then the
    /// failure surfaces with the cancellation as the INNER exception behind the layer's own
    /// exception type, exactly as the AWS SDK client and the HTTP transport stack report it.
    /// </summary>
    private sealed class CancellationWrappingStream(
        ReadbackCancellationStorageService owner,
        byte[] content,
        ReadbackCancellationStyle style) : MemoryStream(content, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.ReadEntered.TrySetResult();
            OperationCanceledException cancellation;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException caught)
            {
                cancellation = caught;
            }

            throw style switch
            {
                ReadbackCancellationStyle.ClientWrapped => new AmazonClientException(
                    $"The request was aborted: {ChatAttachmentReadbackCancellationTests.LeakMarker}", cancellation),
                _ => new IOException(
                    $"Unable to read data from the transport connection: {ChatAttachmentReadbackCancellationTests.LeakMarker}", cancellation),
            };
        }
    }
}

internal static class ReadbackCancellationFixtures
{
    public static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Cancellation Repro",
                email = $"cancel-repro-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public static async Task<StoredFileDto> UploadImage(HttpClient client, AuthResponse auth, string fileName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngFixture());
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

    public static async Task<ConversationDto> CreateConversation(HttpClient client, AuthResponse auth)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations")
        {
            Content = JsonContent.Create(new { title = "Cancellation repro conversation" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    // Valid 128-byte 1x1 RGB PNG; preserve the streamed cancellation fixture length.
    private static byte[] PngFixture() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAAL3RFWHRGaXh0dXJlAHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eNxAqDMAAAAMSURBVHicY/jPwAAAAwEBAMn+ku8AAAAASUVORK5CYII=");
}
