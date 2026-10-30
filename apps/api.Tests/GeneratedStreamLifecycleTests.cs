using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free generated-video storage lifecycle tests. The fake sink models
/// an S3 client which stops reading at Content-Length without probing EOF.
/// </summary>
public sealed class GeneratedStreamLifecycleTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Incorrect_nonseekable_video_length_is_failed_and_object_is_cleaned_up(int actualBytes)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var (workspaceId, userId) = await SeedWorkspaceAsync(db);
        const int declaredBytes = 4;
        var sink = new LengthBoundedFakeStorage();
        var options = Options.Create(new FileOptions { MaxGeneratedVideoBytes = 16 });
        var service = new FileProcessingService(
            db,
            new FileValidationService(options),
            sink,
            new FileContentExtractor(options),
            options,
            NullLogger<FileProcessingService>.Instance);

        var bytes = Enumerable.Range(0, actualBytes).Select(i => (byte)(i + 1)).ToArray();
        await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            service.StoreGeneratedStreamWithResultAsync(
                workspaceId,
                userId,
                projectId: null,
                fileName: "fixture.mp4",
                contentType: "video/mp4",
                sizeBytes: declaredBytes,
                openReadAsync: _ => Task.FromResult<Stream>(new NonSeekableFixtureStream(bytes)),
                metadataJson: null,
                cancellationToken: CancellationToken.None));

        var file = Assert.Single(await db.StoredFiles.AsNoTracking().ToListAsync());
        Assert.Equal(StoredFileStatus.Failed, file.Status);
        Assert.NotNull(file.ProcessedAt);
        Assert.Null(file.ContentHashSha256);
        Assert.Equal(declaredBytes, sink.LastExpectedContentLength);
        Assert.Equal(file.StorageKey, Assert.Single(sink.DeletedKeys));
        Assert.Equal(Math.Min(actualBytes, declaredBytes), sink.BytesAccepted);
        Assert.Empty(await db.GenerationJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Provider_failure_after_partial_video_write_fails_lifecycle_and_attempts_cleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var (workspaceId, userId) = await SeedWorkspaceAsync(db);
        var sink = new LengthBoundedFakeStorage { FailAfterFirstByte = true };
        var options = Options.Create(new FileOptions { MaxGeneratedVideoBytes = 16 });
        var service = new FileProcessingService(
            db,
            new FileValidationService(options),
            sink,
            new FileContentExtractor(options),
            options,
            NullLogger<FileProcessingService>.Instance);

        await Assert.ThrowsAsync<FileStorageOperationException>(() =>
            service.StoreGeneratedStreamWithResultAsync(
                workspaceId,
                userId,
                projectId: null,
                fileName: "fixture.mp4",
                contentType: "video/mp4",
                sizeBytes: 4,
                openReadAsync: _ => Task.FromResult<Stream>(new NonSeekableFixtureStream([1, 2, 3, 4])),
                metadataJson: null,
                cancellationToken: CancellationToken.None));

        var file = Assert.Single(await db.StoredFiles.AsNoTracking().ToListAsync());
        Assert.Equal(StoredFileStatus.Failed, file.Status);
        Assert.NotNull(file.ProcessedAt);
        Assert.Equal(1, sink.BytesAccepted);
        Assert.Equal(file.StorageKey, Assert.Single(sink.DeletedKeys));
        Assert.Empty(await db.GenerationJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking().ToListAsync());
    }

    private static async Task<(Guid WorkspaceId, Guid UserId)> SeedWorkspaceAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Generated stream fixture",
            Slug = $"generated-stream-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"generated-stream-{Guid.NewGuid():N}@example.test",
            Email = "generated-stream@example.test",
            NormalizedUserName = $"GENERATED-STREAM-{Guid.NewGuid():N}",
            NormalizedEmail = "GENERATED-STREAM@EXAMPLE.TEST",
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

    private sealed class LengthBoundedFakeStorage : IFileStorageService
    {
        public string ProviderKey => "test-nonseekable-s3";
        public long? LastExpectedContentLength { get; private set; }
        public int BytesAccepted { get; private set; }
        public List<string> DeletedKeys { get; } = [];
        public bool FailAfterFirstByte { get; init; }

        public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
        {
            var counted = Assert.IsType<CountingReadStream>(content);
            Assert.False(content.CanSeek);
            LastExpectedContentLength = counted.ExpectedContentLength;
            Assert.True(LastExpectedContentLength > 0);

            // Match an SDK which reads only the declared Content-Length,
            // potentially overlooking an extra source byte.
            var buffer = new byte[1];
            while (BytesAccepted < LastExpectedContentLength)
            {
                var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                BytesAccepted += read;
                if (FailAfterFirstByte)
                    throw new FileStorageOperationException();
            }
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(storageKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NonSeekableFixtureStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
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
