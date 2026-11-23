using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Assets;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The storage half of row #1128's pairing/dedup problem: what the workspace content-hash
/// uniqueness guarantee actually forces on generated output, and what that leaves unusable.
///
/// <c>FileProcessingService.FinalizeGeneratedFileAsync</c> looks for a Ready row with the same
/// <c>(WorkspaceId, ContentHashSha256)</c> and, when it finds one, deletes its own object, marks its own
/// row Deleted with the hash nulled (that nulling is what keeps the filtered unique index satisfied) and
/// hands the caller the pre-existing row. The lookup does not consider scope or ownership, so an
/// identical generation by a second member can be bound to a row that member cannot read. Because the
/// index allows only one live row per (workspace, bytes), no write-side change can give both members
/// their own readable copy without either paying storage, changing the scope model in a migration, or
/// widening file visibility. These lanes pin all three facts so the decision is made on evidence, and
/// they run against the same PostgreSQL the API CI job provides because the assertion is about a
/// provider-enforced unique index.
/// </summary>
public sealed class GeneratedOutputCrossScopeAdoptionTests : IAsyncLifetime
{
    private const string IndexName = "IX_StoredFiles_WorkspaceId_ContentHashSha256";
    private readonly string schema = $"dedup_scope_{Guid.NewGuid():N}";
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), $"taslim-dedup-scope-{Guid.NewGuid():N}");
    private DbContextOptions<TaslimDbContext> options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(storageRoot);
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }
        options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
            .Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(storageRoot))
        {
            try { Directory.Delete(storageRoot, recursive: true); }
            catch (IOException) { /* a throwaway storage root must never fail a run */ }
        }
    }

    [Fact]
    public async Task Identical_generated_bytes_adopt_another_members_personal_row_and_leave_that_member_unable_to_open_them()
    {
        var (workspaceId, owner, requester, _) = await SeedAsync();
        var content = Content("personal");
        await using (var db = new TaslimDbContext(options))
        {
            db.StoredFiles.Add(PrivateRow(workspaceId, owner, content, projectId: null));
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger();
        Guid adoptedFileId;
        await using (var db = new TaslimDbContext(options))
        {
            var result = await Service(db, logger).StoreGeneratedWithResultAsync(
                workspaceId, requester, projectId: null, "output.json", "application/json", content, null, CancellationToken.None);
            adoptedFileId = result.File.Id;
            Assert.False(result.WasCreated);
            var own = await db.StoredFiles.AsNoTracking().SingleAsync(file => file.Id != adoptedFileId && file.WorkspaceId == workspaceId);
            Assert.Null(own.ContentHashSha256);
            Assert.Equal(StoredFileStatus.Deleted, own.Status);
            var adopted = await db.StoredFiles.AsNoTracking().Include(file => file.Project).SingleAsync(file => file.Id == adoptedFileId);
            Assert.Equal(owner, adopted.UserId);
            // The consumer decision every read path asks: same answer as the download route.
            Assert.False(AssetService.CanReadStoredFile(adopted, requester, workspaceId));
        }
        Assert.Contains(logger.Warnings, warning => warning.Contains("outside the requester's file visibility", StringComparison.Ordinal)
            && warning.Contains($"RequesterId={requester}", StringComparison.Ordinal)
            && warning.Contains($"ExistingOwnerId={owner}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identical_generated_bytes_adopt_a_conversation_backed_row_that_the_adopting_member_cannot_open()
    {
        var (workspaceId, owner, requester, projectId) = await SeedAsync();
        var content = Content("conversation");
        var conversationId = Guid.NewGuid();
        await using (var db = new TaslimDbContext(options))
        {
            AddConversation(db, conversationId, workspaceId, owner);
            var row = PrivateRow(workspaceId, owner, content, projectId);
            row.ConversationId = conversationId;
            db.StoredFiles.Add(row);
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger();
        await using (var db = new TaslimDbContext(options))
        {
            var result = await Service(db, logger).StoreGeneratedWithResultAsync(
                workspaceId, requester, projectId, "output.json", "application/json", content, null, CancellationToken.None);
            Assert.False(result.WasCreated);
            var adopted = await db.StoredFiles.AsNoTracking().Include(file => file.Project).SingleAsync(file => file.Id == result.File.Id);
            Assert.Equal(conversationId, adopted.ConversationId);
            // A conversation file is private to its owner even when its project is shared, so the
            // adopting member's asset is a dead end here too, and this is the shape #1128 tried to
            // prevent by comparing project columns.
            Assert.False(AssetService.CanReadStoredFile(adopted, requester, workspaceId));
        }
        Assert.Contains(logger.Warnings, warning => warning.Contains("outside the requester's file visibility", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identical_generated_bytes_adopt_a_same_workspace_project_row_that_stays_openable_and_silent()
    {
        var (workspaceId, owner, requester, projectId) = await SeedAsync();
        var content = Content("project");
        await using (var db = new TaslimDbContext(options))
        {
            db.StoredFiles.Add(PrivateRow(workspaceId, owner, content, projectId));
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger();
        await using (var db = new TaslimDbContext(options))
        {
            var result = await Service(db, logger).StoreGeneratedWithResultAsync(
                workspaceId, requester, projectId, "output.json", "application/json", content, null, CancellationToken.None);
            Assert.False(result.WasCreated);
            var adopted = await db.StoredFiles.AsNoTracking().Include(file => file.Project).SingleAsync(file => file.Id == result.File.Id);
            // The ordinary, healthy case: sharing one physical copy inside a workspace is exactly
            // what dedup is for, and a project-scoped file is visible to every member of it.
            Assert.True(AssetService.CanReadStoredFile(adopted, requester, workspaceId));
        }
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("outside the requester's file visibility", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeating_identical_output_adopts_the_requesters_own_row_without_a_warning()
    {
        var (workspaceId, owner, _, _) = await SeedAsync();
        var content = Content("own");
        Guid firstId;
        var firstLogger = new CapturingLogger();
        await using (var db = new TaslimDbContext(options))
        {
            var first = await Service(db, firstLogger).StoreGeneratedWithResultAsync(
                workspaceId, owner, projectId: null, "output.json", "application/json", content, null, CancellationToken.None);
            firstId = first.File.Id;
            Assert.True(first.WasCreated);
        }
        Assert.Empty(firstLogger.Warnings);

        var secondLogger = new CapturingLogger();
        await using (var db = new TaslimDbContext(options))
        {
            var second = await Service(db, secondLogger).StoreGeneratedWithResultAsync(
                workspaceId, owner, projectId: null, "output.json", "application/json", content, null, CancellationToken.None);
            Assert.False(second.WasCreated);
            Assert.Equal(firstId, second.File.Id);
            Assert.True(AssetService.CanReadStoredFile(second.File, owner, workspaceId));
        }
        Assert.Empty(secondLogger.Warnings);
    }

    [Fact]
    public async Task A_second_live_row_for_the_same_bytes_is_rejected_by_the_workspace_unique_hash_index()
    {
        var (workspaceId, owner, _, _) = await SeedAsync();
        var content = Content("collision");
        await using (var db = new TaslimDbContext(options))
        {
            db.StoredFiles.Add(PrivateRow(workspaceId, owner, content, projectId: null));
            await db.SaveChangesAsync();
        }

        // This is why the pairing/dedup conflict cannot be repaired on the write side without a
        // decision: a per-member copy of the same bytes - the shape that would keep every asset
        // readable - is refused by the schema. The assertion is the blocker, not a failing test.
        await using (var db = new TaslimDbContext(options))
        {
            var other = await db.Users.AsNoTracking().FirstAsync();
            db.StoredFiles.Add(PrivateRow(workspaceId, other.Id, content, projectId: null, id: Guid.NewGuid()));
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(IndexName, exception.ToString(), StringComparison.Ordinal);
        }

        await using (var db = new TaslimDbContext(options))
        {
            var live = await db.StoredFiles.CountAsync(file => file.WorkspaceId == workspaceId
                && file.Status == StoredFileStatus.Ready
                && file.ContentHashSha256 != null);
            Assert.Equal(1, live);
        }
    }

    private static ReadOnlyMemory<byte> Content(string tag) =>
        Encoding.UTF8.GetBytes($"{{\"tag\":\"{tag}\",\"synthetic\":true}}");

    private static StoredFile PrivateRow(
        Guid workspaceId,
        Guid ownerUserId,
        ReadOnlyMemory<byte> content,
        Guid? projectId,
        Guid? id = null)
    {
        var fileId = id ?? Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(content.Span));
        return new StoredFile
        {
            Id = fileId,
            WorkspaceId = workspaceId,
            UserId = ownerUserId,
            ProjectId = projectId,
            OriginalFileName = "seed.json",
            StoredFileName = $"seed-{fileId:N}.json",
            ContentType = "application/json",
            Extension = ".json",
            SizeBytes = content.Length,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"tests/{fileId:N}",
            Status = StoredFileStatus.Ready,
            TextExtractionStatus = FileExtractionStatus.Ready,
            ContentHashSha256 = hash,
            ContainerFormat = "json",
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow,
        };
    }

    private async Task<(Guid WorkspaceId, Guid Owner, Guid Requester, Guid ProjectId)> SeedAsync()
    {
        var workspaceId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using var db = new TaslimDbContext(options);
        AddWorkspace(db, workspaceId);
        AddUser(db, owner, "dedup-owner");
        AddUser(db, requester, "dedup-requester");
        AddProject(db, projectId, workspaceId);
        AddMembership(db, workspaceId, owner);
        AddMembership(db, workspaceId, requester);
        await db.SaveChangesAsync();
        return (workspaceId, owner, requester, projectId);
    }

    private FileProcessingService Service(TaslimDbContext db, CapturingLogger logger)
    {
        var fileOptions = Options.Create(new Taslim.Api.Files.FileOptions { LocalRootPath = storageRoot });
        return new FileProcessingService(
            db,
            new FileValidationService(fileOptions),
            new RecordingStorage(),
            new FileContentExtractor(fileOptions),
            fileOptions,
            logger);
    }

    private static void AddWorkspace(TaslimDbContext db, Guid workspaceId) =>
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Dedup scope workspace",
            Slug = $"dedup-scope-{workspaceId:N}",
            Type = WorkspaceType.Business,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddUser(TaslimDbContext db, Guid userId, string email) =>
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"{email}-{userId:N}@example.com",
            Email = $"{email}-{userId:N}@example.com",
            DisplayName = email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddProject(TaslimDbContext db, Guid projectId, Guid workspaceId) =>
        db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            Name = "Dedup scope project",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddMembership(TaslimDbContext db, Guid workspaceId, Guid userId) =>
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });

    private static void AddConversation(TaslimDbContext db, Guid conversationId, Guid workspaceId, Guid userId) =>
        db.Conversations.Add(new Conversation
        {
            Id = conversationId,
            WorkspaceId = workspaceId,
            UserId = userId,
            Title = "Dedup scope conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private sealed class RecordingStorage : IFileStorageService
    {
        public string ProviderKey => FileStorageProviders.Local;
        public List<string> DeletedKeys { get; } = [];
        public Task StoreAsync(string storageKey, Stream stream, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(storageKey);
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CapturingLogger : ILogger<FileProcessingService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
