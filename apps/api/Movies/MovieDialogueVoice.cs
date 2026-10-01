using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Assets;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieDialogueLineStatuses
{
    public const string Draft = "Draft";
    public const string Queued = "Queued";
    public const string Generating = "Generating";
    public const string Ready = "Ready";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Archived = "Archived";
}

public static class MovieDialogueTakeStatuses
{
    public const string Planned = "Planned";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Selected = "Selected";
    public const string Superseded = "Superseded";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Planned, Queued, Running, Succeeded, Failed, Cancelled, Approved, Rejected, Selected, Superseded,
    };
}

public static class MovieDialogueTakeLifecycle
{
    public static bool CanApprove(string status) => status is MovieDialogueTakeStatuses.Succeeded or MovieDialogueTakeStatuses.Rejected;
    public static bool CanSelect(string status) => status is MovieDialogueTakeStatuses.Approved;
    public static bool HasPublishedAsset(Guid? assetId, Guid? storedFileId) => assetId.HasValue && storedFileId.HasValue;
}

public static class MovieDialogueApprovalDecisions
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class MovieDialogueLanguages
{
    public const string English = "en";
    public const string Arabic = "ar";
    public const string KurdishSorani = "ku";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        English, Arabic, KurdishSorani,
    };
}

public sealed class MovieDialogueLine
{
    public Guid Id { get; set; }
    public Guid MovieClipId { get; set; }
    public Guid? MovieCharacterId { get; set; }
    public Guid? SelectedTakeId { get; set; }
    public int Sequence { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string Language { get; set; } = MovieDialogueLanguages.English;
    public string Text { get; set; } = string.Empty;
    public int StartMilliseconds { get; set; }
    public int EndMilliseconds { get; set; }
    public string? DeliveryNotes { get; set; }
    public string Status { get; set; } = MovieDialogueLineStatuses.Draft;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieClip MovieClip { get; set; } = null!;
    public MovieCharacter? MovieCharacter { get; set; }
    public MovieDialogueTake? SelectedTake { get; set; }
    public ICollection<MovieDialogueTake> Takes { get; set; } = [];
}

public sealed class MovieDialogueTake
{
    public Guid Id { get; set; }
    public Guid MovieDialogueLineId { get; set; }
    public Guid MovieClipId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? StoredFileId { get; set; }
    public int VersionNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = MovieDialogueTakeStatuses.Planned;
    public int? DurationMilliseconds { get; set; }
    public string? MetadataJson { get; set; }
    public string? UsageMetadataJson { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? SelectedAt { get; set; }
    public Guid? SelectedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieDialogueLine DialogueLine { get; set; } = null!;
    public MovieClip MovieClip { get; set; } = null!;
    public GenerationJob? GenerationJob { get; set; }
    public Asset? Asset { get; set; }
    public StoredFile? StoredFile { get; set; }
    public ICollection<MovieDialogueTakeApproval> Approvals { get; set; } = [];
}

public sealed class MovieDialogueTakeApproval
{
    public Guid Id { get; set; }
    public Guid MovieDialogueTakeId { get; set; }
    public Guid UserId { get; set; }
    public string Decision { get; set; } = MovieDialogueApprovalDecisions.Approved;
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieDialogueTake Take { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}

public sealed class MovieDialogueVoiceOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "disabled";
    public string OutputFormat { get; set; } = "mp3";
    public string Currency { get; set; } = UsageCurrencies.Usd;
    public int MaxTextCharacters { get; set; } = 10_000;
    public int MaxDeliveryNotesCharacters { get; set; } = 2_000;
    public int MaxOutputBytes { get; set; } = 10 * 1_048_576;
    public int MaxDurationMilliseconds { get; set; } = 900_000;
}

public sealed record MovieDialogueVoiceInput(
    Guid MovieDialogueLineId,
    Guid MovieClipId,
    Guid MovieProjectId,
    Guid? MovieCharacterId,
    string SpeakerName,
    string Text,
    string Language,
    int StartMilliseconds,
    int EndMilliseconds,
    string? DeliveryNotes,
    Guid MovieDialogueTakeId);

public sealed record MovieDialogueVoiceProviderRequest(
    Guid MovieDialogueLineId,
    Guid MovieClipId,
    Guid? MovieCharacterId,
    string SpeakerName,
    string Text,
    string Language,
    int StartMilliseconds,
    int EndMilliseconds,
    string? DeliveryNotes);

public sealed record MovieDialogueVoiceProviderUsage(
    string ModelKey,
    int InputCharacters,
    int OutputBytes,
    int LatencyMilliseconds,
    decimal? EstimatedCostUsd = null,
    decimal? ActualCostUsd = null,
    string? Currency = null,
    string? CostBasis = null,
    string? SafeMetadataJson = null);

public sealed record MovieDialogueVoiceProviderResult(
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string Format,
    int DurationMilliseconds,
    MovieDialogueVoiceProviderUsage Usage);

public interface IMovieDialogueVoiceProvider
{
    string Key { get; }
    Task<MovieDialogueVoiceProviderResult> GenerateAsync(MovieDialogueVoiceProviderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Production-safe default. It never calls an external service or fabricates an audio asset.</summary>
public sealed class UnavailableMovieDialogueVoiceProvider : IMovieDialogueVoiceProvider
{
    public string Key => "disabled";
    public Task<MovieDialogueVoiceProviderResult> GenerateAsync(MovieDialogueVoiceProviderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<MovieDialogueVoiceProviderResult>(new MovieDialogueVoiceProviderUnavailableException());
}

/// <summary>Deterministic in-process adapter intended only for tests and local lifecycle validation.</summary>
public sealed class DeterministicMovieDialogueVoiceProvider : IMovieDialogueVoiceProvider
{
    private static readonly byte[] Audio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];
    public string Key => "fake";
    public Task<MovieDialogueVoiceProviderResult> GenerateAsync(MovieDialogueVoiceProviderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var duration = Math.Max(1, request.EndMilliseconds - request.StartMilliseconds);
        return Task.FromResult(new MovieDialogueVoiceProviderResult(
            Audio,
            "audio/mpeg",
            "mp3",
            duration,
            new MovieDialogueVoiceProviderUsage("fake-dialogue-voice", request.Text.Length, Audio.Length, 0, 0m, 0m, UsageCurrencies.Usd, UsageCostBasis.Actual,
                JsonSerializer.Serialize(new { inputCharacters = request.Text.Length, outputBytes = Audio.Length }))));
    }
}

public sealed class MovieDialogueVoiceProviderUnavailableException() : Exception("Movie dialogue voice execution is disabled.");
public sealed class MovieDialogueVoiceOutputInvalidException() : Exception("The dialogue voice adapter returned invalid audio.");
public sealed class MovieDialogueVoiceRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieDialogueVoiceGenerationJobHandler(
    IEnumerable<IMovieDialogueVoiceProvider> providers,
    IOptions<MovieDialogueVoiceOptions> options,
    ILogger<MovieDialogueVoiceGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly MovieDialogueVoiceOptions settings = options.Value;
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.MovieDialogueVoiceGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        MovieDialogueVoiceInput input;
        try
        {
            input = JsonSerializer.Deserialize<MovieDialogueVoiceInput>(job.InputJson)
                ?? throw new MovieDialogueVoiceRequestValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue voice request is invalid.");
        }
        catch (JsonException)
        {
            throw new MovieDialogueVoiceRequestValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue voice request is invalid.");
        }
        Validate(input);
        if (!settings.Enabled) throw new MovieDialogueVoiceProviderUnavailableException();
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new MovieDialogueVoiceProviderUnavailableException();
        progress.Report(15);
        var started = DateTime.UtcNow;
        var generated = await provider.GenerateAsync(new MovieDialogueVoiceProviderRequest(
            input.MovieDialogueLineId, input.MovieClipId, input.MovieCharacterId, input.SpeakerName, input.Text, input.Language,
            input.StartMilliseconds, input.EndMilliseconds, input.DeliveryNotes), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOutput(generated);
        progress.Report(85);
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Audio,
            contentType = generated.ContentType.Trim().ToLowerInvariant(),
            format = generated.Format.Trim().ToLowerInvariant(),
            dialogueLineId = input.MovieDialogueLineId,
            movieClipId = input.MovieClipId,
            movieCharacterId = input.MovieCharacterId,
            speakerName = input.SpeakerName,
            language = input.Language,
            startMilliseconds = input.StartMilliseconds,
            endMilliseconds = input.EndMilliseconds,
            durationMilliseconds = generated.DurationMilliseconds,
            sizeBytes = generated.Content.Length,
        });
        var artifact = new GeneratedFileArtifact($"dialogue-{job.Id:N}.{generated.Format.Trim().ToLowerInvariant()}", generated.ContentType.Trim().ToLowerInvariant(), generated.Content, metadata);
        var usage = new AiUsageMetadata(
            provider.Key,
            generated.Usage.ModelKey,
            generated.Usage.InputCharacters,
            null,
            generated.Usage.OutputBytes,
            generated.Usage.EstimatedCostUsd,
            generated.Usage.ActualCostUsd,
            Math.Max(1, generated.Usage.LatencyMilliseconds > 0 ? generated.Usage.LatencyMilliseconds : (int)Math.Min(int.MaxValue, (DateTime.UtcNow - started).TotalMilliseconds)),
            "completed",
            false,
            Currency: generated.Usage.Currency,
            CostBasis: generated.Usage.CostBasis,
            SafeMetadataJson: generated.Usage.SafeMetadataJson);
        var result = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Audio,
            dialogueLineId = input.MovieDialogueLineId,
            movieClipId = input.MovieClipId,
            language = input.Language,
            startMilliseconds = input.StartMilliseconds,
            endMilliseconds = input.EndMilliseconds,
            durationMilliseconds = generated.DurationMilliseconds,
        });
        progress.Report(95);
        logger.LogInformation("Movie dialogue voice output validated. JobId={JobId}; DialogueLineId={DialogueLineId}; SizeBytes={SizeBytes}", job.Id, input.MovieDialogueLineId, generated.Content.Length);
        return new GenerationHandlerResult(result,
        [new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            metadata,
            artifact,
            new GeneratedAssetDescriptor($"{input.SpeakerName} dialogue take", "Movie dialogue voice take.", AssetTypes.Audio, metadata))], usage);
    }

    private void Validate(MovieDialogueVoiceInput input)
    {
        if (input.MovieDialogueLineId == Guid.Empty || input.MovieClipId == Guid.Empty || input.MovieProjectId == Guid.Empty || input.MovieDialogueTakeId == Guid.Empty
            || string.IsNullOrWhiteSpace(input.SpeakerName) || input.SpeakerName.Length > 160
            || string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > Math.Max(1, settings.MaxTextCharacters)
            || !MovieDialogueLanguages.Supported.Contains(input.Language)
            || input.StartMilliseconds < 0 || input.EndMilliseconds <= input.StartMilliseconds
            || input.EndMilliseconds - input.StartMilliseconds > Math.Max(1, settings.MaxDurationMilliseconds)
            || input.DeliveryNotes?.Length > Math.Max(0, settings.MaxDeliveryNotesCharacters))
            throw new MovieDialogueVoiceRequestValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue voice request is invalid.");
    }

    private void ValidateOutput(MovieDialogueVoiceProviderResult output)
    {
        var maximum = Math.Min(10 * 1_048_576, Math.Max(1, settings.MaxOutputBytes));
        if (output.Content.Length <= 0 || output.Content.Length > maximum || output.DurationMilliseconds <= 0
            || string.IsNullOrWhiteSpace(output.ContentType) || !output.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(output.Format) || output.Format.Length > 12)
            throw new MovieDialogueVoiceOutputInvalidException();
    }
}

public sealed class MovieDialogueVoiceExecutionStore(TaslimDbContext db)
{
    public async Task MarkRunningAsync(Guid jobId, Guid concurrencyToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var updated = await db.MovieDialogueTakes
            .Where(item => item.GenerationJobId == jobId
                && (item.Status == MovieDialogueTakeStatuses.Planned || item.Status == MovieDialogueTakeStatuses.Queued)
                && db.GenerationJobs.Any(job => job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueTakeStatuses.Running).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated > 0)
            await db.MovieDialogueLines.Where(item => item.Takes.Any(take => take.GenerationJobId == jobId)).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueLineStatuses.Generating).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkReadyAsync(Guid jobId, Guid concurrencyToken, Guid? assetId, Guid? storedFileId, int? durationMilliseconds, string? metadataJson, string? usageMetadataJson, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var updated = await db.MovieDialogueTakes
            .Where(item => item.GenerationJobId == jobId && db.GenerationJobs.Any(job => job.Id == jobId && job.Status == GenerationJobStatus.Succeeded && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, MovieDialogueTakeStatuses.Succeeded)
                .SetProperty(item => item.AssetId, assetId)
                .SetProperty(item => item.StoredFileId, storedFileId)
                .SetProperty(item => item.DurationMilliseconds, durationMilliseconds)
                .SetProperty(item => item.MetadataJson, metadataJson)
                .SetProperty(item => item.UsageMetadataJson, usageMetadataJson)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated > 0)
            await db.MovieDialogueLines.Where(item => item.Takes.Any(take => take.GenerationJobId == jobId)).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueLineStatuses.Ready).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkFailedAsync(Guid jobId, string? failureCode, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieDialogueTakes.Where(item => item.GenerationJobId == jobId && !new[] { MovieDialogueTakeStatuses.Approved, MovieDialogueTakeStatuses.Selected, MovieDialogueTakeStatuses.Superseded }.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueTakeStatuses.Failed).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        await db.MovieDialogueLines.Where(item => item.Takes.Any(take => take.GenerationJobId == jobId)).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueLineStatuses.Draft).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieDialogueTakes.Where(item => item.GenerationJobId == jobId && !new[] { MovieDialogueTakeStatuses.Approved, MovieDialogueTakeStatuses.Selected, MovieDialogueTakeStatuses.Superseded }.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueTakeStatuses.Cancelled).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        await db.MovieDialogueLines.Where(item => item.Takes.Any(take => take.GenerationJobId == jobId)).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueLineStatuses.Draft).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }
}

public sealed record MovieDialogueTakeDto(Guid Id, Guid MovieDialogueLineId, int VersionNumber, string Label, string Status, Guid? GenerationJobId, Guid? AssetId, Guid? StoredFileId, int? DurationMilliseconds, string? MetadataJson, DateTime CreatedAt, DateTime UpdatedAt, DateTime? ApprovedAt, DateTime? SelectedAt, IReadOnlyList<MovieDialogueTakeApprovalDto> Approvals);
public sealed record MovieDialogueTakeApprovalDto(Guid Id, Guid UserId, string Decision, string? Comment, DateTime CreatedAt);
public sealed record MovieDialogueLineDto(Guid Id, Guid MovieClipId, Guid? MovieCharacterId, Guid? SelectedTakeId, int Sequence, string SpeakerName, string Language, string Text, int StartMilliseconds, int EndMilliseconds, string? DeliveryNotes, string Status, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<MovieDialogueTakeDto> Takes);
public sealed record MovieDialogueClipDto(Guid MovieClipId, Guid MovieProjectId, IReadOnlyList<MovieDialogueLineDto> Lines);
public sealed record MovieDialogueTakeResponse(MovieDialogueTakeDto Take, GenerationJobDto Job);
public sealed class MovieDialogueLineRequest
{
    public Guid? MovieCharacterId { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string Language { get; set; } = MovieDialogueLanguages.English;
    public string Text { get; set; } = string.Empty;
    public int StartMilliseconds { get; set; }
    public int EndMilliseconds { get; set; }
    public string? DeliveryNotes { get; set; }
}
public sealed class MovieDialogueTakeRequest
{
    public string? Label { get; set; }
}
public sealed class MovieDialogueTakeApprovalRequest
{
    public string Decision { get; set; } = MovieDialogueApprovalDecisions.Approved;
    public string? Comment { get; set; }
}

public interface IMovieDialogueProductionService
{
    Task<MovieDialogueClipDto?> GetClipAsync(Guid userId, Guid clipId, CancellationToken cancellationToken);
    Task<MovieDialogueLineDto?> AddLineAsync(Guid userId, Guid clipId, MovieDialogueLineRequest request, CancellationToken cancellationToken);
    Task<MovieDialogueTakeResponse?> QueueTakeAsync(Guid userId, Guid lineId, MovieDialogueTakeRequest request, string? idempotencyKey, CancellationToken cancellationToken);
    Task<MovieDialogueLineDto?> ApproveTakeAsync(Guid userId, Guid takeId, MovieDialogueTakeApprovalRequest request, CancellationToken cancellationToken);
    Task<MovieDialogueLineDto?> SelectTakeAsync(Guid userId, Guid takeId, CancellationToken cancellationToken);
}

public sealed class MovieDialogueProductionService(
    TaslimDbContext db,
    MovieAuthorizationService authorization,
    IGenerationJobService jobs) : IMovieDialogueProductionService
{
    public async Task<MovieDialogueClipDto?> GetClipAsync(Guid userId, Guid clipId, CancellationToken cancellationToken)
    {
        var clip = await QueryClip().SingleOrDefaultAsync(item => item.Id == clipId, cancellationToken);
        if (clip is null || !await authorization.CanPermissionAsync(userId, clip.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToDto(clip);
    }

    public async Task<MovieDialogueLineDto?> AddLineAsync(Guid userId, Guid clipId, MovieDialogueLineRequest request, CancellationToken cancellationToken)
    {
        var clip = await db.MovieClips.Include(item => item.MovieProject).SingleOrDefaultAsync(item => item.Id == clipId, cancellationToken);
        if (clip is null || !await authorization.CanPermissionAsync(userId, clip.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateLine(request);
        var speakerName = request.SpeakerName.Trim();
        if (request.MovieCharacterId is Guid characterId)
        {
            var character = await db.MovieCharacters.AsNoTracking().SingleOrDefaultAsync(item => item.Id == characterId && item.MovieProjectId == clip.MovieProjectId, cancellationToken);
            if (character is null) throw new MovieDialogueValidationException("MOVIE_DIALOGUE_CHARACTER_INVALID", "The speaker character does not belong to this movie project.");
            if (string.IsNullOrWhiteSpace(speakerName)) speakerName = character.Name;
        }
        var now = DateTime.UtcNow;
        var line = new MovieDialogueLine
        {
            Id = Guid.NewGuid(), MovieClipId = clipId, MovieCharacterId = request.MovieCharacterId,
            Sequence = (await db.MovieDialogueLines.Where(item => item.MovieClipId == clipId).MaxAsync(item => (int?)item.Sequence, cancellationToken) ?? 0) + 1,
            SpeakerName = speakerName, Language = NormalizeLanguage(request.Language), Text = request.Text.Trim(),
            StartMilliseconds = request.StartMilliseconds, EndMilliseconds = request.EndMilliseconds,
            DeliveryNotes = Clean(request.DeliveryNotes), Status = MovieDialogueLineStatuses.Draft,
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        db.MovieDialogueLines.Add(line);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(await QueryLine().SingleAsync(item => item.Id == line.Id, cancellationToken));
    }

    public async Task<MovieDialogueTakeResponse?> QueueTakeAsync(Guid userId, Guid lineId, MovieDialogueTakeRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var line = await db.MovieDialogueLines.Include(item => item.MovieClip).ThenInclude(item => item.MovieProject).SingleOrDefaultAsync(item => item.Id == lineId, cancellationToken);
        if (line is null || !await authorization.CanAsync(userId, line.MovieClip.MovieProjectId, MovieOperationalActions.Generate, cancellationToken)) return null;
        if (request.Label?.Length > 160) throw new MovieDialogueValidationException("MOVIE_DIALOGUE_TAKE_INVALID", "The take label must be 160 characters or fewer.");
        var take = new MovieDialogueTake
        {
            Id = Guid.NewGuid(), MovieDialogueLineId = line.Id, MovieClipId = line.MovieClipId,
            VersionNumber = (await db.MovieDialogueTakes.Where(item => item.MovieDialogueLineId == line.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1,
            Label = string.IsNullOrWhiteSpace(request.Label) ? $"Take {((await db.MovieDialogueTakes.Where(item => item.MovieDialogueLineId == line.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1)}" : request.Label.Trim(),
            Status = MovieDialogueTakeStatuses.Planned, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MovieDialogueTakes.Add(take);
        await db.SaveChangesAsync(cancellationToken);
        var input = new MovieDialogueVoiceInput(line.Id, line.MovieClipId, line.MovieClip.MovieProjectId, line.MovieCharacterId, line.SpeakerName, line.Text, line.Language, line.StartMilliseconds, line.EndMilliseconds, line.DeliveryNotes, take.Id);
        GenerationJob job;
        try
        {
            job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = line.MovieClip.MovieProject.WorkspaceId,
                ProjectId = line.MovieClip.MovieProject.ProjectId,
                JobType = GenerationJobTypes.MovieDialogueVoiceGenerate,
                Title = take.Label,
                InputJson = JsonSerializer.Serialize(input),
            }, cancellationToken, idempotencyKey);
        }
        catch
        {
            db.MovieDialogueTakes.Remove(take);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        take.GenerationJobId = job.Id;
        take.Status = MovieDialogueTakeStatuses.Queued;
        take.UpdatedAt = DateTime.UtcNow;
        line.Status = MovieDialogueLineStatuses.Queued;
        line.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieDialogueTakeResponse(ToDto(await QueryLine().SingleAsync(item => item.Id == line.Id, cancellationToken)).Takes.Single(item => item.Id == take.Id), GenerationJobContractMapper.ToMovieDto(job));
    }

    public async Task<MovieDialogueLineDto?> ApproveTakeAsync(Guid userId, Guid takeId, MovieDialogueTakeApprovalRequest request, CancellationToken cancellationToken)
    {
        var take = await db.MovieDialogueTakes.Include(item => item.DialogueLine).ThenInclude(item => item.MovieClip).SingleOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        if (take is null || !await authorization.CanAsync(userId, take.DialogueLine.MovieClip.MovieProjectId, MovieOperationalActions.TakeApproval, cancellationToken)) return null;
        var decision = request.Decision?.Trim() ?? string.Empty;
        if (decision is not (MovieDialogueApprovalDecisions.Approved or MovieDialogueApprovalDecisions.Rejected) || request.Comment?.Length > 4_000)
            throw new MovieDialogueValidationException("MOVIE_DIALOGUE_APPROVAL_INVALID", "Choose an approval decision and keep the comment within 4,000 characters.");
        if (!MovieDialogueTakeLifecycle.CanApprove(take.Status)) throw new MovieDialogueValidationException("MOVIE_DIALOGUE_TAKE_NOT_REVIEWABLE", "Only a completed dialogue take can be approved.");
        var now = DateTime.UtcNow;
        db.MovieDialogueTakeApprovals.Add(new MovieDialogueTakeApproval { Id = Guid.NewGuid(), MovieDialogueTakeId = take.Id, UserId = userId, Decision = decision, Comment = Clean(request.Comment), CreatedAt = now });
        take.Status = decision == MovieDialogueApprovalDecisions.Approved ? MovieDialogueTakeStatuses.Approved : MovieDialogueTakeStatuses.Rejected;
        take.ApprovedAt = decision == MovieDialogueApprovalDecisions.Approved ? now : null;
        take.ApprovedByUserId = decision == MovieDialogueApprovalDecisions.Approved ? userId : null;
        take.UpdatedAt = now;
        take.DialogueLine.Status = decision == MovieDialogueApprovalDecisions.Approved ? MovieDialogueLineStatuses.Approved : MovieDialogueLineStatuses.Rejected;
        take.DialogueLine.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(await QueryLine().SingleAsync(item => item.Id == take.MovieDialogueLineId, cancellationToken));
    }

    public async Task<MovieDialogueLineDto?> SelectTakeAsync(Guid userId, Guid takeId, CancellationToken cancellationToken)
    {
        var take = await db.MovieDialogueTakes.Include(item => item.DialogueLine).ThenInclude(item => item.MovieClip).SingleOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        if (take is null || !await authorization.CanAsync(userId, take.DialogueLine.MovieClip.MovieProjectId, MovieOperationalActions.TakeSelect, cancellationToken)) return null;
        if (!MovieDialogueTakeLifecycle.CanSelect(take.Status)) throw new MovieDialogueValidationException("MOVIE_DIALOGUE_TAKE_NOT_APPROVED", "Approve the dialogue take before selecting it.");
        var now = DateTime.UtcNow;
        var previous = await db.MovieDialogueTakes.Where(item => item.MovieDialogueLineId == take.MovieDialogueLineId && item.Id != take.Id && item.Status == MovieDialogueTakeStatuses.Selected).ToListAsync(cancellationToken);
        foreach (var item in previous) { item.Status = MovieDialogueTakeStatuses.Superseded; item.SelectedAt = null; item.SelectedByUserId = null; item.UpdatedAt = now; }
        take.Status = MovieDialogueTakeStatuses.Selected;
        take.SelectedAt = now;
        take.SelectedByUserId = userId;
        take.UpdatedAt = now;
        take.DialogueLine.SelectedTakeId = take.Id;
        take.DialogueLine.Status = MovieDialogueLineStatuses.Approved;
        take.DialogueLine.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(await QueryLine().SingleAsync(item => item.Id == take.MovieDialogueLineId, cancellationToken));
    }

    private IQueryable<MovieClip> QueryClip() => db.MovieClips.AsNoTracking().Include(item => item.DialogueLines).ThenInclude(item => item.Takes).ThenInclude(item => item.Approvals);
    private IQueryable<MovieDialogueLine> QueryLine() => db.MovieDialogueLines.AsNoTracking().Include(item => item.Takes).ThenInclude(item => item.Approvals);
    private static MovieDialogueClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieProjectId, clip.DialogueLines.OrderBy(item => item.Sequence).Select(ToDto).ToArray());
    private static MovieDialogueLineDto ToDto(MovieDialogueLine line) => new(line.Id, line.MovieClipId, line.MovieCharacterId, line.SelectedTakeId, line.Sequence, line.SpeakerName, line.Language, line.Text, line.StartMilliseconds, line.EndMilliseconds, line.DeliveryNotes, line.Status, line.CreatedAt, line.UpdatedAt, line.Takes.OrderBy(item => item.VersionNumber).Select(ToDto).ToArray());
    private static MovieDialogueTakeDto ToDto(MovieDialogueTake take) => new(take.Id, take.MovieDialogueLineId, take.VersionNumber, take.Label, take.Status, take.GenerationJobId, take.AssetId, take.StoredFileId, take.DurationMilliseconds, take.MetadataJson, take.CreatedAt, take.UpdatedAt, take.ApprovedAt, take.SelectedAt, take.Approvals.OrderByDescending(item => item.CreatedAt).Select(item => new MovieDialogueTakeApprovalDto(item.Id, item.UserId, item.Decision, item.Comment, item.CreatedAt)).ToArray());
    private static void ValidateLine(MovieDialogueLineRequest request)
    {
        if ((string.IsNullOrWhiteSpace(request.SpeakerName) && !request.MovieCharacterId.HasValue) || request.SpeakerName.Length > 160 || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10_000
            || !MovieDialogueLanguages.Supported.Contains(request.Language?.Trim() ?? string.Empty) || request.StartMilliseconds < 0 || request.EndMilliseconds <= request.StartMilliseconds
            || request.EndMilliseconds - request.StartMilliseconds > 900_000 || request.DeliveryNotes?.Length > 2_000)
            throw new MovieDialogueValidationException("MOVIE_DIALOGUE_LINE_INVALID", "The dialogue speaker, language, text, and timing must be valid.");
    }
    private static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class MovieDialogueValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MovieDialogueVoiceExecutionMetadata(int? DurationMilliseconds, string? MetadataJson, string? UsageMetadataJson);
