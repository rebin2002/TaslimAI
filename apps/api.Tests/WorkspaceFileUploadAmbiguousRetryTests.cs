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
/// API-level synthetic regression for the retry half of production issue #459.
/// The incident: a Chat PDF upload through <c>POST /api/workspaces/{workspaceId}/files</c>
/// returned HTTP 503 after <c>S3CompatibleFileStorageService.StoreAsync</c> threw
/// <see cref="AmazonS3Exception"/> with <c>StatusCode = 0</c>, <c>ErrorCode = null</c> and
/// <c>RequestId = null</c>, and the user was told "Please try again".
/// A transport-level status-0 failure is *ambiguous*: the provider may have committed the
/// object before the response was lost, so a retry runs against storage that might already
/// contain the first attempt's bytes. The companion suite
/// <c>WorkspaceFileUploadStorageFailureTests</c> (open PR #768) pins the unambiguous case
/// where every PUT is refused and nothing is ever stored. This file covers the distinct
/// committed-but-unacknowledged outcome and its retry:
/// 1. the ambiguous attempt must terminate as a Failed row and must never surface a Ready
///    attachment, even though its PUT actually committed at the provider;
/// 2. the user's retry must produce exactly one Ready attachment row and exactly one durable
///    object copy for those bytes in the bucket - a retry can never duplicate a persisted
///    attachment, and the fail-closed cleanup must remove the ambiguously-committed object;
/// 3. even when orphan cleanup itself fails, the retry must still leave only one visible
///    Ready attachment and must never expose the stale object through the failed file's API
///    identity.
/// Everything is in-process and synthetic: the SDK client seam
/// (<see cref="IS3CompatibleObjectClient"/>) is a fake bucket; no live R2/S3 endpoint,
/// credentials, provider traffic, or production data is involved.
/// </summary>
public sealed class WorkspaceFileUploadAmbiguousRetryTests
    : IClassFixture<WorkspaceFileUploadAmbiguousRetryTests.AmbiguousOutcomeS3Api>
{
    // Synthetic, non-resolvable values; they exist only so the adapter runs with an
    // S3-configured profile in-process. They are never contacted.
    private const string SyntheticEndpoint = "https://synthetic-r2-endpoint.invalid";
    private const string SyntheticBucket = "synthetic-ambiguity-retry-bucket";
    private const string SyntheticAccessKey = "synthetic-access-key-not-secret";
    private const string SyntheticSecretKey = "synthetic-secret-key-not-secret";
    private const string StableUploadFailureMessage = "We couldn't store this file. Please try again.";

    private readonly AmbiguousOutcomeS3Api api;

    public WorkspaceFileUploadAmbiguousRetryTests(AmbiguousOutcomeS3Api api)
    {
        this.api = api;
        using var scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Retry_after_ambiguous_committed_put_persists_exactly_one_ready_attachment_and_one_object_copy()
    {
        using var client = api.CreateClient();
        var auth = await RegisterAsync(client, "Ambiguity Retry Owner");
        var pdf = CreatePdf("Ambiguous retry dedup marker " + Guid.NewGuid().ToString("N"));

        Guid failedId;
        string failedKey;
        try
        {
            // Attempt 1: the PUT commits at the provider, then the response is lost -
            // exactly the incident signature (transport failure, no HTTP response seen).
            api.ObjectClient.FailPutAfterCommit = true;
            var first = await UploadPdfAsync(client, auth.PersonalWorkspace.Id, pdf);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            var body = await first.Content.ReadAsStringAsync();
            using (var json = JsonDocument.Parse(body))
            {
                var error = json.RootElement.GetProperty("error");
                Assert.Equal("FILE_STORAGE_OPERATION_FAILED", error.GetProperty("code").GetString());
                Assert.Equal(StableUploadFailureMessage, error.GetProperty("message").GetString());
            }

            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var row = Assert.Single(db.StoredFiles.Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id));
                Assert.Equal(StoredFileStatus.Failed, row.Status);
                Assert.Equal(FileExtractionStatus.Failed, row.TextExtractionStatus);
                Assert.Equal(FileStorageProviders.S3Compatible, row.StorageProvider);
                failedId = row.Id;
                failedKey = row.StorageKey;
            }

            // The ambiguity is real: the object was durably committed under the failed
            // attempt's key. Fail-closed hygiene must delete that committed orphan before
            // the API admits the retry, or the retry would duplicate a persisted attachment.
            Assert.Contains(failedKey, api.ObjectClient.PutAttemptedKeys);
            Assert.Contains(failedKey, api.ObjectClient.DeleteAttemptedKeys);
            Assert.False(api.ObjectClient.TryGetStored(failedKey, out _));

            // Attempt 2: the user follows the on-screen instruction and retries with the
            // same bytes over a healthy transport.
            api.ObjectClient.FailPutAfterCommit = false;
            var second = await UploadPdfAsync(client, auth.PersonalWorkspace.Id, pdf);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            var dto = await second.Content.ReadFromJsonAsync<StoredFileDto>();
            Assert.NotNull(dto);
            Assert.Equal("Ready", dto.Status);
            Assert.Equal("Ready", dto.TextExtractionStatus);
            Assert.NotEqual(failedId, dto.Id);
        }
        finally
        {
            api.ObjectClient.FailPutAfterCommit = false;
            api.ObjectClient.FailDelete = false;
        }

        string readyKey;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var rows = db.StoredFiles.Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, row => row.Status == StoredFileStatus.Ready);
            Assert.DoesNotContain(rows, row => row.Status
                is StoredFileStatus.Processing
                or StoredFileStatus.Uploading);
            readyKey = rows.Single(row => row.Status == StoredFileStatus.Ready).StorageKey;
        }

        // Provider-level dedup: the whole synthetic bucket holds exactly one object, the
        // retry's key, and exactly one copy of the attachment bytes exists. The ambiguously
        // committed first-attempt object was not duplicated by the retry and was not revived.
        Assert.Equal(1, api.ObjectClient.ObjectCount);
        Assert.True(api.ObjectClient.TryGetStored(readyKey, out var storedBytes));
        Assert.True(storedBytes!.SequenceEqual(pdf));
        Assert.Equal(1, api.ObjectClient.CountWithBytes(pdf));
        Assert.DoesNotContain(failedKey, api.ObjectClient.StoredKeysSnapshot);

        // User-visible dedup: the workspace list exposes one Ready attachment plus the
        // terminal Failed residue, and the failed identity never resolves to Ready content.
        var listed = await client.GetFromJsonAsync<List<StoredFileDto>>(
            $"/api/workspaces/{auth.PersonalWorkspace.Id}/files");
        Assert.NotNull(listed);
        Assert.Equal(1, listed.Count(file => file.Status == "Ready"));
        Assert.Single(listed, file => file.Status == "Failed");
        using var failedView = await client.GetAsync($"/api/files/{failedId}");
        Assert.Equal(HttpStatusCode.OK, failedView.StatusCode);
        var failedDto = await failedView.Content.ReadFromJsonAsync<StoredFileDto>();
        Assert.NotNull(failedDto);
        Assert.Equal("Failed", failedDto.Status);
    }

    [Fact]
    public async Task Retry_after_ambiguous_503_leaves_one_visible_ready_attachment_even_when_orphan_cleanup_also_fails()
    {
        using var client = api.CreateClient();
        var auth = await RegisterAsync(client, "Ambiguity Cleanup Failure Owner");
        var pdf = CreatePdf("Ambiguous cleanup-failure marker " + Guid.NewGuid().ToString("N"));

        Guid failedId;
        string failedKey;
        try
        {
            // Attempt 1: the PUT commits, the response is lost, and the fail-closed
            // delete of the committed orphan also fails at the transport layer.
            api.ObjectClient.FailPutAfterCommit = true;
            api.ObjectClient.FailDelete = true;
            var first = await UploadPdfAsync(client, auth.PersonalWorkspace.Id, pdf);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var row = Assert.Single(db.StoredFiles.Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id));
                Assert.Equal(StoredFileStatus.Failed, row.Status);
                failedId = row.Id;
                failedKey = row.StorageKey;
            }

            Assert.Contains(failedKey, api.ObjectClient.DeleteAttemptedKeys);
            // The stale object may survive a failed cleanup; the invariant below is that
            // it can never become (or be duplicated as) a user-visible persisted attachment.
            Assert.True(api.ObjectClient.TryGetStored(failedKey, out _));

            api.ObjectClient.FailPutAfterCommit = false;
            api.ObjectClient.FailDelete = false;
            var second = await UploadPdfAsync(client, auth.PersonalWorkspace.Id, pdf);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            var dto = await second.Content.ReadFromJsonAsync<StoredFileDto>();
            Assert.NotNull(dto);
            Assert.Equal("Ready", dto.Status);
            Assert.NotEqual(failedId, dto!.Id);
        }
        finally
        {
            api.ObjectClient.FailPutAfterCommit = false;
            api.ObjectClient.FailDelete = false;
        }

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var rows = db.StoredFiles.Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, row => row.Status == StoredFileStatus.Ready);
        }

        // The ambiguous attempt stays a terminal Failed identity forever: neither the
        // metadata endpoint nor the workspace list ever treats its committed-but-stale
        // object as a second Ready attachment.
        using var failedView = await client.GetAsync($"/api/files/{failedId}");
        Assert.Equal(HttpStatusCode.OK, failedView.StatusCode);
        var failedDto = await failedView.Content.ReadFromJsonAsync<StoredFileDto>();
        Assert.NotNull(failedDto);
        Assert.Equal("Failed", failedDto.Status);
        Assert.Equal("Failed", failedDto.TextExtractionStatus);

        var listed = await client.GetFromJsonAsync<List<StoredFileDto>>(
            $"/api/workspaces/{auth.PersonalWorkspace.Id}/files");
        Assert.NotNull(listed);
        Assert.Equal(1, listed.Count(file => file.Status == "Ready"));

        // Exactly one Ready-attributable object key exists; the stale orphan is not
        // referenced by any Ready row after the retry.
        Assert.DoesNotContain(failedKey, api.ObjectClient.StoredKeysSnapshot.Where(key =>
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            return db.StoredFiles.Any(row =>
                row.WorkspaceId == auth.PersonalWorkspace.Id
                && row.StorageKey == key
                && row.Status == StoredFileStatus.Ready);
        }));
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string name)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName = name,
            email = $"upload-ambiguous-retry-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> UploadPdfAsync(HttpClient client, Guid workspaceId, byte[] pdf)
    {
        // A syntactically valid PDF so the failure under test is storage transport,
        // never a file-type validation rejection (issue #459 evidence).
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
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

    /// <summary>
    /// Builds a minimal structurally valid single-page PDF whose text layer contains
    /// <paramref name="text"/>, so a healthy retry run reaches the Ready state through
    /// the real extractor rather than an extraction-failed Failed state.
    /// </summary>
    private static byte[] CreatePdf(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
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
        foreach (var offset in offsets.Skip(1))
            WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));

    /// <summary>
    /// Runs the real <see cref="S3CompatibleFileStorageService"/> adapter and the full
    /// upload pipeline behind the HTTP API, with the SDK client seam replaced by an
    /// in-process fake bucket that can commit a PUT and then lose the response, exactly
    /// like the #459 ambiguity (no HTTP response, so StatusCode=0 with null ErrorCode
    /// and RequestId).
    /// </summary>
    public sealed class AmbiguousOutcomeS3Api : TaslimApiFactory
    {
        public AmbiguousOutcomeS3Api()
        {
            ObjectClient = new AmbiguousOutcomeObjectClient();
        }

        internal AmbiguousOutcomeObjectClient ObjectClient { get; }

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
            });
        }
    }

    internal sealed class AmbiguousOutcomeObjectClient : IS3CompatibleObjectClient
    {
        private readonly Dictionary<string, byte[]> objects = new();

        public List<string> PutAttemptedKeys { get; } = new();
        public List<string> DeleteAttemptedKeys { get; } = new();

        /// <summary>When set, the next PUT commits bytes into the fake bucket and then
        /// throws the incident transport exception, modelling a provider-committed
        /// request whose response was never acknowledged.</summary>
        public bool FailPutAfterCommit { get; set; }

        /// <summary>When set, DELETE throws a transport failure so orphan cleanup fails.</summary>
        public bool FailDelete { get; set; }

        public int ObjectCount => objects.Count;

        public IReadOnlyList<string> StoredKeysSnapshot => objects.Keys.ToList();

        public bool TryGetStored(string key, out byte[]? bytes) => objects.TryGetValue(key, out bytes);

        public int CountWithBytes(byte[] expected) =>
            objects.Values.Count(stored => stored.SequenceEqual(expected));

        public async Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            PutAttemptedKeys.Add(key);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            // The provider durably commits the object regardless of what the client learns.
            objects[key] = buffer.ToArray();
            if (!FailPutAfterCommit) return;

            // Recorded incident signature: transport-level failure with no HTTP response,
            // so StatusCode=0, ErrorCode=null, RequestId=null.
            throw new AmazonS3Exception(
                "synthetic ambiguous committed PUT: response lost before acknowledgement",
                new HttpRequestException("synthetic transport reset after provider commit"))
            {
                StatusCode = (HttpStatusCode)0,
            };
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            if (!objects.TryGetValue(key, out var bytes))
            {
                throw new AmazonS3Exception("synthetic missing object")
                {
                    StatusCode = HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey",
                };
            }
            return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            DeleteAttemptedKeys.Add(key);
            if (FailDelete)
            {
                throw new AmazonS3Exception("synthetic delete transport failure")
                {
                    StatusCode = (HttpStatusCode)0,
                };
            }
            objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(objects.ContainsKey(key));

        public void Dispose()
        {
        }
    }
}
