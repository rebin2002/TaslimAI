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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

public sealed class S3CompatibleStorageTests
{
    [Fact]
    public async Task Store_open_exists_and_delete_use_configured_bucket_and_guid_key()
    {
        var client = new FakeObjectClient();
        var service = CreateService(client);
        const string key = "workspace-id/file-id/file-id.txt";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("r2-content"));

        await service.StoreAsync(key, input);
        Assert.Equal(("taslim-private", key), client.LastPut);
        Assert.True(await service.ExistsAsync(key));

        await using (var output = await service.OpenReadAsync(key))
        using (var reader = new StreamReader(output!))
        {
            Assert.Equal("r2-content", await reader.ReadToEndAsync());
        }

        await service.DeleteAsync(key);
        Assert.Equal(("taslim-private", key), client.LastDelete);
        Assert.False(await service.ExistsAsync(key));
    }

    [Fact]
    public async Task Provider_failures_become_safe_storage_errors_without_secret_text()
    {
        var client = new FakeObjectClient
        {
            Failure = new AmazonS3Exception("provider failure contains r2-secret-value")
            {
                StatusCode = HttpStatusCode.Forbidden,
                ErrorCode = "AccessDenied",
                RequestId = "r2-request-123",
            },
        };
        var logger = new RecordingLogger();
        var service = CreateService(client, logger);

        var exception = await Assert.ThrowsAsync<FileStorageOperationException>(() => service.StoreAsync("workspace/file", new MemoryStream([1, 2, 3])));

        Assert.DoesNotContain("r2-secret-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("taslim-secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Messages, message => message.Contains("Operation=put", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("StatusCode=403", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("ErrorCode=AccessDenied", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("ErrorType=AmazonS3Exception", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("RequestId=r2-request-123", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("r2-secret-value", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("taslim-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_provider_operation_failure_returns_safe_operation_error_code()
    {
        using var factory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "S3Compatible",
            ["Files:S3Endpoint"] = "https://account.r2.cloudflarestorage.com",
            ["Files:S3Region"] = "auto",
            ["Files:S3Bucket"] = "taslim-private",
            ["Files:S3AccessKey"] = "access-key-for-test",
            ["Files:S3SecretKey"] = "secret-key-for-test",
        }, new FailingStorageService(FileStorageProviders.S3Compatible, new FileStorageOperationException()));
        EnsureDatabase(factory);
        using var client = factory.CreateClient();
        var auth = await Register(client, "Configured provider failure");
        var response = await Upload(client, auth.PersonalWorkspace.Id, "brief.txt", "content");
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("FILE_STORAGE_OPERATION_FAILED", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("We couldn't store this file. Please try again.", error.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unconfigured_provider_retains_unavailable_error_code()
    {
        using var factory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "UnknownProvider",
        });
        EnsureDatabase(factory);
        using var client = factory.CreateClient();
        var auth = await Register(client, "Unconfigured provider");
        var response = await Upload(client, auth.PersonalWorkspace.Id, "brief.txt", "content");
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("FILE_STORAGE_UNAVAILABLE", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("File storage is not configured yet.", error.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Local_provider_remains_selected_for_development_factory()
    {
        using var factory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "Local",
            ["Files:LocalRootPath"] = "App_Data/test-files",
        });

        var service = factory.Services.GetRequiredService<IFileStorageService>();
        Assert.IsType<LocalFileStorageService>(service);
    }

    [Fact]
    public void Complete_s3_provider_selects_real_adapter_without_falling_back_to_local()
    {
        using var factory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "S3Compatible",
            ["Files:S3Endpoint"] = "https://account.r2.cloudflarestorage.com",
            ["Files:S3Region"] = "auto",
            ["Files:S3Bucket"] = "taslim-private",
            ["Files:S3AccessKey"] = "access-key-for-test",
            ["Files:S3SecretKey"] = "secret-key-for-test",
        });

        var service = factory.Services.GetRequiredService<IFileStorageService>();
        Assert.IsType<S3CompatibleFileStorageService>(service);
        Assert.False(service is LocalFileStorageService);
        ((IDisposable)service).Dispose();
    }

    [Fact]
    public void Unknown_or_incomplete_provider_selects_unavailable_service()
    {
        using var unknownFactory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "UnknownProvider",
        });
        var unknown = unknownFactory.Services.GetRequiredService<IFileStorageService>();
        Assert.IsType<UnconfiguredFileStorageService>(unknown);
        Assert.False(unknown is LocalFileStorageService);

        using var incompleteFactory = new StorageProviderFactory(new Dictionary<string, string?>
        {
            ["Files:StorageProvider"] = "S3Compatible",
            ["Files:S3Endpoint"] = "https://account.r2.cloudflarestorage.com",
            ["Files:S3Region"] = "auto",
            ["Files:S3Bucket"] = "taslim-private",
        });
        var incomplete = incompleteFactory.Services.GetRequiredService<IFileStorageService>();
        Assert.IsType<UnconfiguredFileStorageService>(incomplete);
        Assert.False(incomplete is LocalFileStorageService);
    }

    private static S3CompatibleFileStorageService CreateService(FakeObjectClient client, ILogger<S3CompatibleFileStorageService>? logger = null) =>
        new(client, Options.Create(new FileOptions
        {
            StorageProvider = FileStorageProviders.S3Compatible,
            S3Endpoint = "https://account.r2.cloudflarestorage.com",
            S3Region = "auto",
            S3Bucket = "taslim-private",
            S3AccessKey = "access-key-for-test",
            S3SecretKey = "secret-key-for-test",
        }), logger ?? NullLogger<S3CompatibleFileStorageService>.Instance);

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"storage-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, string fileName, string content)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
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

    private static void EnsureDatabase(StorageProviderFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    private sealed class RecordingLogger : ILogger<S3CompatibleFileStorageService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger<S3CompatibleFileStorageService>.Instance.BeginScope(state)!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class FakeObjectClient : IS3CompatibleObjectClient
    {
        private readonly ConcurrentDictionary<(string Bucket, string Key), byte[]> objects = new();
        public (string Bucket, string Key)? LastPut { get; private set; }
        public (string Bucket, string Key)? LastDelete { get; private set; }
        public Exception? Failure { get; init; }

        public async Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[(bucket, key)] = buffer.ToArray();
            LastPut = (bucket, key);
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult<Stream?>(objects.TryGetValue((bucket, key), out var content) ? new MemoryStream(content, writable: false) : null);
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            objects.TryRemove((bucket, key), out _);
            LastDelete = (bucket, key);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(objects.ContainsKey((bucket, key)));
        }

        public void Dispose() { }

        private void ThrowIfConfigured()
        {
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class FailingStorageService(string providerKey, Exception failure) : IFileStorageService
    {
        public string ProviderKey => providerKey;
        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => throw failure;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw failure;
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => throw failure;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class StorageProviderFactory(IReadOnlyDictionary<string, string?> values, IFileStorageService? storageOverride = null) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            connection.Open();
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddSingleton(connection);
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
                if (storageOverride is not null)
                {
                    services.RemoveAll<IFileStorageService>();
                    services.AddSingleton(storageOverride);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) connection.Dispose();
        }
    }
}
