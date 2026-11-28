using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free contract regression for issue #677 (execution/price fidelity).
///
/// Invariant protected here: a NON-DEFAULT priced quality/resolution/upscaling
/// intent must never be silently accepted when the selected video adapter cannot
/// honor it. For every non-default priced intent the job handler must either
///   (a) reject fail-closed with MOVIE_PROVIDER_UNSUPPORTED_REQUEST before the
///       selected adapter is instructed, leaving no submitted provider operation,
///       no returned usage, and no usage transaction or credit ledger debit, or
///   (b) submit only when the effective request faithfully represents the priced
///       intent (target resolution, quality tier, upscaling target) so the one
///       canonical debit matches what was priced.
///
/// The default/absent-intent path must keep being accepted with exactly one
/// canonical submission and exactly one canonical usage candidate — guarding
/// against an over-eager fix that rejects defaults or double-books usage.
///
/// Ownership: this is a new, independently owned test file only; it edits no
/// production source. Per #677 the fail-closed/mapping guard belongs to the
/// existing owner of <c>MovieVideoExecution.cs</c> (PR #269). On current main
/// the second test is expected RED: the handler forwards <c>UpscalingRequested</c>
/// but drops <c>TargetResolution</c> (it sends the never-populated
/// <c>MasterResolution</c>) and <c>MovieVideoGenerationRequest</c> carries no
/// <c>QualityTier</c> field, so a priced 2160p/Studio/upscale intent is submitted
/// to an adapter that receives no equivalent instruction while a debit-shaped
/// usage is returned. That silent acceptance is the defect under contract.
///
/// No real Runway/Manus provider calls, no paid inference, no customer charges,
/// no production resources. The adapter is a local capturing fake.
/// </summary>
public sealed class MovieVideoQualityIntentContract677Tests
{
    [Fact]
    public async Task DefaultPricedIntent_IsAcceptedAndCarriesExactlyOneCanonicalUsage()
    {
        // Mirrors the legacy QueueClipAsync route on current main: the serialized
        // MovieGenerationInput carries no resolution/quality/upscaling intent, so
        // every cost-relevant intent stays at its default (null / false).
        var (provider, handler, db, job, clip, priced) = await SetupAsync(static input => input);

        var result = await handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);

        // Accepted default path: exactly one canonical instruction to the adapter.
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(0, provider.CancelCalls);
        var captured = Assert.Single(provider.CapturedRequests);

        // Nothing may be silently injected on the default path either.
        Assert.Null(captured.SourceResolution);
        Assert.Null(captured.MasterResolution);
        Assert.Null(captured.ProcessingPath);
        Assert.False(captured.UpscalingRequested);

        // Disjunctive #677 contract holds trivially: defaults impose no representation
        // obligation, so the request must be accepted and be intent-consistent.
        Assert.Empty(MissingIntentRepresentations(priced, captured));

        // Exactly one canonical usage candidate flows to the ledger owner, and the
        // handler writes no usage transaction or credit ledger row itself — proving
        // the accepted path reserves/debits through the single canonical path only.
        Assert.NotNull(result.Usage);
        Assert.Equal(provider.Key, result.Usage!.ProviderKey);
        Assert.Equal(0, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());

        // One canonical execution row, durably submitted; clip advanced, not Ready.
        var execution = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Equal("contract677-provider-job", execution.ProviderJobId);
        var storedClip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == clip.Id);
        Assert.Equal(MovieClipStatuses.Generating, storedClip.Status);
    }

    [Fact]
    public async Task NonDefaultPricedQualityResolutionUpscaleIntent_IsRejectedOrFaithfullyExecuted_NotSilentlyAccepted()
    {
        // Reproduces the intent an approved production render persists today
        // (MovieProductionGenerationOrchestrator.QueueApprovedAsync) for a priced
        // 720p -> 2160p, Studio-quality clip, plus explicit upscale intent, while
        // MasterResolution stays unset exactly as production leaves it. The
        // selected adapter is a neutral capturing fake: like the enabled
        // Runway/Manus adapters it declares and honors no priced intent.
        var (provider, handler, db, job, clip, priced) = await SetupAsync(static input => input with
        {
            SourceResolution = MovieResolutionTiers.P720,
            TargetResolution = MovieResolutionTiers.P2160,
            ProcessingPath = MovieResolutionPathKinds.SourceToMaster,
            QualityTier = DirectorQualityLevels.Studio,
            UpscalingRequested = true,
        });

        MovieVideoProviderException? rejection = null;
        GenerationHandlerResult? accepted = null;
        try
        {
            accepted = await handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);
        }
        catch (MovieVideoProviderException exception)
        {
            rejection = exception;
        }

        if (rejection is not null)
        {
            // Fail-closed branch: the stable unsupported-request code the job
            // pipeline already maps (GenerationJobExecution) without any debit.
            Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, rejection.Code);
            Assert.True(provider.SubmitCalls == 0, "A rejected non-default intent must never reach the selected adapter.");
            Assert.Equal(0, provider.CancelCalls);

            // Accounting invariant: rejection leaves no reservation, no debit,
            // no live provider operation, and no Ready clip.
            Assert.Equal(0, await db.UsageTransactions.CountAsync());
            Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());
            Assert.False(await db.MovieVideoProviderExecutions.AsNoTracking().AnyAsync(item => item.ProviderJobId != null),
                "Rejection must leave no live provider operation behind.");
            var rejectedClip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == clip.Id);
            Assert.NotEqual(MovieClipStatuses.Ready, rejectedClip.Status);
            return;
        }

        // Submitted branch: only legitimate when every non-default priced intent
        // is represented on the request the adapter actually received, so the
        // single canonical debit matches what was priced. Reflection-based so a
        // faithful-mapping fix (new request fields) satisfies this without
        // pinning one design.
        var captured = Assert.Single(provider.CapturedRequests);
        var missing = MissingIntentRepresentations(priced, captured);
        Assert.True(
            missing.Count == 0,
            "Issue #677 contract violation — silent acceptance of a non-default priced intent by a selected " +
            $"adapter that cannot honor it. Missing adapter-visible representation for: {string.Join("; ", missing)}. " +
            "The handler must reject fail-closed with MOVIE_PROVIDER_UNSUPPORTED_REQUEST before submission and " +
            "before any usage debit, or map the priced intent faithfully. Fix owner: MovieVideoExecution.cs (PR #269).");

        // If/when the mapping branch exists, accounting must still be single-canonical.
        Assert.NotNull(accepted!.Usage);
        Assert.Equal(0, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());
    }

    /// <summary>
    /// Returns every adapter-visible representation gap between the priced intent
    /// and the request actually submitted. A non-default priced field must equal
    /// the corresponding request value (or, for the target resolution, the
    /// adapter-consumed master/target field) for the intent to be honored.
    /// </summary>
    private static List<string> MissingIntentRepresentations(MovieGenerationInput priced, MovieVideoGenerationRequest captured)
    {
        var missing = new List<string>();
        if (!string.IsNullOrWhiteSpace(priced.TargetResolution)
            && !ValueEquals(priced.TargetResolution, RepresentedValue(captured, "TargetResolution", nameof(MovieVideoGenerationRequest.MasterResolution))))
        {
            missing.Add($"{nameof(MovieGenerationInput.TargetResolution)}='{priced.TargetResolution}' has no adapter-visible target " +
                $"(request '{nameof(MovieVideoGenerationRequest.MasterResolution)}'={Display(captured.MasterResolution)} and no mapped target field)");
        }
        if (!string.IsNullOrWhiteSpace(priced.QualityTier)
            && !ValueEquals(priced.QualityTier, RepresentedValue(captured, "QualityTier", "QualityTier")))
        {
            missing.Add($"{nameof(MovieGenerationInput.QualityTier)}='{priced.QualityTier}' is absent from {nameof(MovieVideoGenerationRequest)} entirely");
        }
        if (priced.UpscalingRequested)
        {
            if (!captured.UpscalingRequested)
            {
                missing.Add($"{nameof(MovieGenerationInput.UpscalingRequested)}=true was dropped from the submitted request");
            }
            else if (string.IsNullOrWhiteSpace(RepresentedValue(captured, "TargetResolution", nameof(MovieVideoGenerationRequest.MasterResolution))))
            {
                missing.Add($"{nameof(MovieGenerationInput.UpscalingRequested)}=true was forwarded without any adapter-visible upscale target — an instruction no adapter can honor");
            }
        }
        if (!string.IsNullOrWhiteSpace(priced.SourceResolution)
            && !ValueEquals(priced.SourceResolution, captured.SourceResolution))
        {
            missing.Add($"{nameof(MovieGenerationInput.SourceResolution)}='{priced.SourceResolution}' was not carried to the adapter");
        }
        if (!string.IsNullOrWhiteSpace(priced.ProcessingPath)
            && !ValueEquals(priced.ProcessingPath, captured.ProcessingPath))
        {
            missing.Add($"{nameof(MovieGenerationInput.ProcessingPath)}='{priced.ProcessingPath}' was not carried to the adapter");
        }
        return missing;
    }

    /// <summary>
    /// Reads the preferred property if the request record has grown to expose it
    /// (future faithful-mapping fix), otherwise falls back to the field the
    /// current adapter boundary actually consumes.
    /// </summary>
    private static string? RepresentedValue(MovieVideoGenerationRequest request, string preferredProperty, string fallbackProperty)
    {
        var preferred = typeof(MovieVideoGenerationRequest)
            .GetProperty(preferredProperty, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(request) as string;
        return string.IsNullOrWhiteSpace(preferred)
            ? request.GetType().GetProperty(fallbackProperty, BindingFlags.Public | BindingFlags.Instance)?.GetValue(request) as string
            : preferred;
    }

    private static bool ValueEquals(string? expected, string? actual) =>
        string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Display(string? value) => value ?? "null";

    private static async Task<(
        ContractCapturingProvider provider,
        MovieVideoGenerationJobHandler handler,
        TaslimDbContext db,
        GenerationJob job,
        MovieClip clip,
        MovieGenerationInput priced)> SetupAsync(Func<MovieGenerationInput, MovieGenerationInput> shapeInput)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TaslimDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Movie intent contract 677",
            Slug = $"movie-intent-677-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"movie-intent-677-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"MOVIE-INTENT-677-{Guid.NewGuid():N}",
            Email = "movie-intent-677@example.test",
            NormalizedEmail = "MOVIE-INTENT-677@EXAMPLE.TEST",
            DisplayName = "Movie intent contract",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            Title = "Intent fidelity movie",
            Description = "Provider-free #677 contract fixture.",
            DurationSeconds = 5,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), MovieProjectId = Guid.Empty, UpdatedAt = now },
        };
        movie.Guide.MovieProjectId = movie.Id;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Status = GenerationJobStatus.Running,
            InputJson = "{}",
            ConcurrencyToken = Guid.NewGuid(),
            CreatedAt = now,
        };
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            GenerationJobId = job.Id,
            Status = MovieClipStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var input = shapeInput(new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            movie.Id,
            clip.Id,
            null,
            null,
            "A hero shot with a priced delivery intent.",
            5,
            "16:9",
            "cinematic",
            "en",
            null,
            null,
            null,
            null));
        job.InputJson = JsonSerializer.Serialize(input);

        db.AddRange(
            workspace,
            user,
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            },
            movie,
            job,
            clip);
        await db.SaveChangesAsync();

        var provider = new ContractCapturingProvider();
        var options = new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxStatusPolls = 1,
            StatusPollIntervalSeconds = 1,
            MaxTransientRetries = 0,
        };
        var handler = new MovieVideoGenerationJobHandler(
            db,
            provider,
            new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options),
            NullLogger<MovieVideoGenerationJobHandler>.Instance);
        return (provider, handler, db, job, clip, input);
    }

    /// <summary>
    /// Neutral capturing adapter. Implements the same IMovieVideoProvider contract
    /// the enabled Runway/Manus adapters implement today: it declares no priced
    /// capability, inspects none, and always succeeds locally — standing in for a
    /// selected adapter that cannot honor a non-default priced intent. No network,
    /// no paid inference.
    /// </summary>
    private sealed class ContractCapturingProvider : IMovieVideoProvider
    {
        public string Key => "contract677-capturing";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        public List<MovieVideoGenerationRequest> CapturedRequests { get; } = [];
        public int SubmitCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            CapturedRequests.Add(request);
            return Task.FromResult(new MovieVideoSubmission("contract677-provider-job"));
        }

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieVideoProviderStatus(
                MovieVideoProviderJobStatus.Succeeded,
                100,
                "video/mp4",
                "contract677.mp4",
                1024L,
                5,
                null,
                ActualCostUsd: 0.40m,
                Currency: "USD",
                CostBasis: "provider-free-contract-test",
                SafeMetadataJson: null,
                EstimatedCostUsd: 0.50m));

        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieVideoProviderOutput(
                "video/mp4",
                "contract677.mp4",
                1024L,
                _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])),
                5,
                null,
                0.50m,
                0.40m,
                "USD",
                "provider-free-contract-test",
                null,
                "contract677.fake-video"));

        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}
