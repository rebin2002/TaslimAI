using System.Globalization;
using System.Security.Cryptography;
using Amazon.S3.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free composition tests for the generated-video non-seekable S3 PUT path.
/// Unit coverage for request flags, exact-length verification, generic size bounds,
/// and storage error mapping lives on current main; these tests pin only the remaining
/// production-composition seams that those tests do not exercise together.
/// </summary>
public sealed class GeneratedVideoStreamContentLengthRegressionTests
{
    private const string Bucket = "taslim-private";

    [Fact]
    public async Task Generated_video_service_forwards_declared_length_to_nonchunked_put()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var (workspaceId, userId) = await SeedWorkspaceAsync(db);

        var payload = MakeMp4Payload(24);
        var client = new CapturingPutAdapterClient();
        var options = CreateOptions();
        using var storage = new S3CompatibleFileStorageService(
            client, options, NullLogger<S3CompatibleFileStorageService>.Instance);
        var service = CreateProcessingService(db, storage, options);

        var result = await service.StoreGeneratedStreamWithResultAsync(
            workspaceId,
            userId,
            projectId: null,
            fileName: "fixture.mp4",
            contentType: "video/mp4",
            sizeBytes: payload.Length,
            openReadAsync: _ => Task.FromResult<Stream>(new NonSeekableFixtureStream(payload)),
            metadataJson: null,
            cancellationToken: CancellationToken.None);

        var request = Assert.IsType<PutObjectRequest>(client.Captured);
        Assert.Equal(payload.Length.ToString(CultureInfo.InvariantCulture), HeaderOrEmpty(request, "Content-Length"));
        Assert.False(request.UseChunkEncoding);
        Assert.False(request.AutoResetStreamPosition);
        Assert.False(request.AutoCloseStream);
        Assert.Equal(payload, client.AcceptedBytes);
        Assert.Equal(StoredFileStatus.Ready, result.File.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), result.File.ContentHashSha256);
        Assert.Empty(client.DeletedKeys);
    }

    [Fact]
    public async Task Caller_cancellation_during_s3_put_fails_video_and_cleans_partial_object()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var (workspaceId, userId) = await SeedWorkspaceAsync(db);

        var payload = MakeMp4Payload(24);
        using var cancellation = new CancellationTokenSource();
        var client = new CapturingPutAdapterClient { CancelAfterFirstByte = cancellation };
        var options = CreateOptions();
        using var storage = new S3CompatibleFileStorageService(
            client, options, NullLogger<S3CompatibleFileStorageService>.Instance);
        var service = CreateProcessingService(db, storage, options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.StoreGeneratedStreamWithResultAsync(
                workspaceId,
                userId,
                projectId: null,
                fileName: "fixture.mp4",
                contentType: "video/mp4",
                sizeBytes: payload.Length,
                openReadAsync: _ => Task.FromResult<Stream>(new NonSeekableFixtureStream(payload)),
                metadataJson: null,
                cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(client.AcceptedBytes);
        var file = Assert.Single(await db.StoredFiles.AsNoTracking().ToListAsync());
        Assert.Equal(StoredFileStatus.Failed, file.Status);
        Assert.NotNull(file.ProcessedAt);
        Assert.Null(file.ContentHashSha256);
        Assert.Equal(file.StorageKey, Assert.Single(client.DeletedKeys));
    }

    [Fact]
    public async Task Counting_stream_retains_only_the_fixed_sniff_prefix_for_long_nonseekable_input()
    {
        const int payloadLength = 192;
        var payload = Enumerable.Range(0, payloadLength).Select(i => (byte)(i % 251)).ToArray();
        using var counted = new CountingReadStream(
            new NonSeekableFixtureStream(payload), maxBytes: payloadLength);

        await counted.CopyToAsync(Stream.Null);

        Assert.Equal(payloadLength, counted.BytesRead);
        Assert.Equal(64, counted.Prefix.Length);
        Assert.Equal(payload[..64], counted.Prefix.ToArray());
    }

    private static IOptions<FileOptions> CreateOptions() => Options.Create(new FileOptions
    {
        S3Bucket = Bucket,
        MaxGeneratedVideoBytes = 1024 * 1024,
    });

    private static FileProcessingService CreateProcessingService(
        TaslimDbContext db,
        IFileStorageService storage,
        IOptions<FileOptions> options) => new(
            db,
            new FileValidationService(options),
            storage,
            new FileContentExtractor(options),
            options,
            NullLogger<FileProcessingService>.Instance);

    private static byte[] MakeMp4Payload(int length)
    {
        if (length < 12) throw new ArgumentOutOfRangeException(nameof(length));
        var payload = new byte[length];
        payload[3] = (byte)length;
        "ftyp"u8.CopyTo(payload.AsSpan(4));
        "isom"u8.CopyTo(payload.AsSpan(8));
        return payload;
    }

    private static string HeaderOrEmpty(PutObjectRequest request, string name)
    {
        try { return request.Headers[name] ?? string.Empty; }
        catch (KeyNotFoundException) { return string.Empty; }
    }

    private static async Task<(Guid WorkspaceId, Guid UserId)> SeedWorkspaceAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Generated stream fixture",
            Slug = $"generated-stream-{suffix}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"generated-stream-{suffix}@example.test",
            Email = $"generated-stream-{suffix}@example.test",
            NormalizedUserName = $"GENERATED-STREAM-{suffix}@EXAMPLE.TEST",
            NormalizedEmail = $"GENERATED-STREAM-{suffix}@EXAMPLE.TEST",
            DisplayName = "Generated stream tester",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(workspace, user, new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();
        return (workspace.Id, user.Id);
    }

    /// <summary>
    /// Provider-free S3 client fake that runs the real pure request builder and behaves like
    /// a Content-Length-limited SDK consumer; it never makes a network request.
    /// </summary>
    private sealed class CapturingPutAdapterClient : IS3CompatibleObjectClient
    {
        private readonly MemoryStream accepted = new();

        public PutObjectRequest? Captured { get; private set; }
        public byte[] AcceptedBytes => accepted.ToArray();
        public List<string> DeletedKeys { get; } = [];
        public CancellationTokenSource? CancelAfterFirstByte { get; init; }

        public async Task PutAsync(
            string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            var request = AwsS3CompatibleObjectClient.CreatePutObjectRequest(bucket, key, content);
            Captured = request;
            var lengthText = HeaderOrEmpty(request, "Content-Length");
            if (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength)
                || contentLength <= 0)
                throw new InvalidOperationException("The generated upload did not provide a valid Content-Length.");

            var oneByte = new byte[1];
            while (accepted.Length < contentLength)
            {
                var read = await content.ReadAsync(oneByte.AsMemory(), cancellationToken);
                if (read == 0) break;
                accepted.Write(oneByte, 0, read);
                if (accepted.Length == 1) CancelAfterFirstByte?.Cancel();
            }
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public void Dispose() { }
    }

    private sealed class NonSeekableFixtureStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
