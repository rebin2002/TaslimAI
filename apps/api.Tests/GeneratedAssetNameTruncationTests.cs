using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Assets;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The name and description of a generated asset come from provider output, so they can exceed the
/// Asset.Name and Asset.Description column widths and can be built almost entirely from emoji and
/// other supplementary characters. Slicing at the column width then lands between the two halves of
/// a surrogate pair, and the lone surrogate that survives cannot be UTF-8 encoded. That failure only
/// surfaces when the job completion transaction writes the row - after the provider spend is already
/// booked - so the user is told a successful generation failed to store.
///
/// Publication is asserted against the same PostgreSQL the API CI job provides, because that is the
/// provider that UTF-8 encodes on write. The repository's SQLite suites store a lone surrogate
/// silently and stay green on the broken code, so every case here goes through the real publisher.
/// </summary>
public sealed class GeneratedAssetNameTruncationTests : IAsyncLifetime
{
    private readonly string schema = $"asset_name_{Guid.NewGuid():N}";
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), $"taslim-asset-name-{Guid.NewGuid():N}");
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
            try
            {
                Directory.Delete(storageRoot, recursive: true);
            }
            catch (IOException)
            {
                // A throwaway storage root must never be the reason a run fails.
            }
        }
    }

    [DisposablePostgresFact]
    public async Task A_name_cut_between_a_surrogate_pair_is_persisted_as_whole_characters()
    {
        // 127 emoji fill code units 0..253, so the 255th slot is the high half of the next pair and
        // that pair has to stay whole on the far side of the cut.
        var publication = await PublishAsync(string.Concat(Enumerable.Repeat("🙂", 128)), null);

        Assert.NotNull(publication.Asset);
        AssertDoesNotContainUnpairedSurrogates(publication.Asset!.Name);
        Assert.Equal(254, publication.Asset.Name.Length);
        Assert.StartsWith("🙂🙂🙂", publication.Asset.Name, StringComparison.Ordinal);
    }

    [DisposablePostgresFact]
    public async Task The_truncated_name_survives_the_completion_write_that_used_to_abort()
    {
        var (db, job) = await CreateContextWithJobAsync();
        var publication = await CreatePublisher(db).PrepareAsync(job, Output(
            string.Concat(Enumerable.Repeat("🙂", 128)),
            string.Concat(Enumerable.Repeat("🎬", 1_001))));

        Assert.NotNull(publication.Asset);
        db.GenerationJobOutputs.Add(publication.Output);
        db.Assets.Add(publication.Asset);
        db.GeneratedMediaProvenance.Add(publication.Provenance!);
        await db.SaveChangesAsync();

        await using var verify = new TaslimDbContext(options);
        var stored = await verify.Assets.AsNoTracking().SingleAsync(item => item.SourceGenerationJobId == job.Id);
        Assert.Equal(publication.Asset!.Name, stored.Name);
        Assert.Equal(254, stored.Name.Length);
        Assert.Equal(2_000, stored.Description!.Length);
        AssertDoesNotContainUnpairedSurrogates(stored.Name);
        AssertDoesNotContainUnpairedSurrogates(stored.Description!);
    }

    [DisposablePostgresFact]
    public async Task A_name_ending_exactly_on_the_limit_with_a_whole_pair_keeps_every_code_unit()
    {
        // Index 254 is the low half here, so the final character is already complete and the slice
        // may keep all 255 code units.
        var value = string.Concat(Enumerable.Repeat("🙂", 127)) + "x";

        var publication = await PublishAsync(value, null);

        Assert.NotNull(publication.Asset);
        Assert.Equal(GeneratedAssetPublisher.MaximumAssetNameLength, publication.Asset!.Name.Length);
        Assert.Equal(value, publication.Asset.Name);
    }

    [DisposablePostgresFact]
    public async Task A_name_already_at_exactly_the_limit_is_returned_untouched()
    {
        var value = new string('a', GeneratedAssetPublisher.MaximumAssetNameLength);

        var publication = await PublishAsync(value, null);

        Assert.NotNull(publication.Asset);
        Assert.Equal(value, publication.Asset!.Name);
    }

    [DisposablePostgresFact]
    public async Task Truncation_keeps_arabic_and_sorani_letters_intact()
    {
        // Right-to-left prose is the common long-title case and must not be mangled by the limit.
        var publication = await PublishAsync(new string('ع', 400), new string('ک', 2_500));

        Assert.NotNull(publication.Asset);
        Assert.Equal(GeneratedAssetPublisher.MaximumAssetNameLength, publication.Asset!.Name.Length);
        Assert.StartsWith("ع", publication.Asset.Name, StringComparison.Ordinal);
        Assert.Equal(GeneratedAssetPublisher.MaximumAssetDescriptionLength, publication.Asset.Description!.Length);
        AssertDoesNotContainUnpairedSurrogates(publication.Asset.Name);
    }

    [DisposablePostgresFact]
    public async Task A_provider_that_omits_the_name_still_gets_a_persistable_fallback()
    {
        var publication = await PublishAsync("   ", null);

        Assert.NotNull(publication.Asset);
        Assert.Equal("Generated asset", publication.Asset!.Name);
        Assert.Null(publication.Asset.Description);
    }

    [DisposablePostgresFact]
    public async Task A_short_name_and_description_pass_through_the_publisher_unchanged()
    {
        var publication = await PublishAsync("تقرير أداء الربع 🙂", "ملخص مختصر");

        Assert.NotNull(publication.Asset);
        Assert.Equal("تقرير أداء الربع 🙂", publication.Asset!.Name);
        Assert.Equal("ملخص مختصر", publication.Asset.Description);
    }

    // ---------------------------------------------------------------

    private static void AssertDoesNotContainUnpairedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Assert.True(index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    $"Code unit {index} is a high surrogate with no low surrogate after it.");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[index]), $"Code unit {index} is an orphaned low surrogate.");
            }
        }
    }

    private async Task<PreparedGenerationOutput> PublishAsync(string name, string? description)
    {
        var (db, job) = await CreateContextWithJobAsync();
        return await CreatePublisher(db).PrepareAsync(job, Output(name, description));
    }

    /// <summary>
    /// Every case owns its workspace, user and job, so a run never depends on how the
    /// content-addressed storage dedupes identical synthetic bytes shared between cases.
    /// </summary>
    private async Task<(TaslimDbContext Db, GenerationJob Job)> CreateContextWithJobAsync()
    {
        var db = new TaslimDbContext(options);
        var now = DateTime.UtcNow;
        var seed = Guid.NewGuid().ToString("N");
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Asset name truncation",
            Slug = $"asset-name-{seed}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"asset-name-{seed}@example.com",
            Email = $"asset-name-{seed}@example.com",
            DisplayName = "Asset name truncation tester",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            JobType = GenerationJobTypes.SystemTest,
            Status = GenerationJobStatus.Running,
            InputJson = "{}",
            CreatedAt = now,
            StartedAt = now,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync();
        return (db, job);
    }

    private GeneratedAssetPublisher CreatePublisher(TaslimDbContext db)
    {
        var fileOptions = Options.Create(new Taslim.Api.Files.FileOptions { LocalRootPath = storageRoot });
        var files = new FileProcessingService(
            db,
            new FileValidationService(fileOptions),
            new LocalFileStorageService(fileOptions, NullLogger<LocalFileStorageService>.Instance),
            new FileContentExtractor(fileOptions),
            fileOptions,
            NullLogger<FileProcessingService>.Instance);
        return new GeneratedAssetPublisher(
            db,
            files,
            new GenerationQualityControlService(
                Options.Create(new GenerationQualityControlOptions()),
                NullLogger<GenerationQualityControlService>.Instance),
            new NoopGeneratedMediaRetentionHook(),
            NullLogger<GeneratedAssetPublisher>.Instance);
    }

    private static GenerationHandlerOutput Output(string name, string? description) => new(
        GenerationJobOutputTypes.StoredFile,
        null,
        null,
        new GeneratedFileArtifact("output.png", "image/png", Png(320, 180)),
        new GeneratedAssetDescriptor(name, description, AssetTypes.Image));

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[40];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        "IEND"u8.CopyTo(bytes.AsSpan(32, 4));
        return bytes;
    }
}
