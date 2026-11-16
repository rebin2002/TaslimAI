using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieProductionCheckpointStates
{
    public const string NotStarted = "NotStarted";
    public const string Active = "Active";
    public const string Running = "Running";
    public const string Blocked = "Blocked";
    public const string Recoverable = "Recoverable";
    public const string Complete = "Complete";
}

public static class MovieProductionCheckpointActions
{
    public const string CreateStoryboard = "CreateStoryboard";
    public const string ReviewStoryboard = "ReviewStoryboard";
    public const string CreateKeyframe = "CreateKeyframe";
    public const string ReviewKeyframe = "ReviewKeyframe";
    public const string CreateMotionPreview = "CreateMotionPreview";
    public const string ReviewMotionPreview = "ReviewMotionPreview";
    public const string CreateRender = "CreateRender";
    public const string CreateTake = "CreateTake";
    public const string Retry = "Retry";
}

public sealed class MovieProductionCheckpoint
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public int Version { get; set; }
    public string State { get; set; } = MovieProductionCheckpointStates.NotStarted;
    public int ProgressPercent { get; set; }
    public int TotalShots { get; set; }
    public int CompletedShots { get; set; }
    public int RunningShots { get; set; }
    public int BlockedShots { get; set; }
    public int RecoverableShots { get; set; }
    public int PendingApprovalShots { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public DateTime? LastRecoveredAt { get; set; }
    public Guid? LastRecoveredByUserId { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
}

public sealed record MovieProductionCheckpointVersionSnapshot(
    Guid Id,
    string Stage,
    string Status,
    Guid? GenerationJobId,
    bool HasAsset);

public sealed record MovieProductionCheckpointTakeSnapshot(
    Guid Id,
    string Status,
    bool IsSelected,
    bool IsFinal,
    bool HasAsset,
    Guid? GenerationJobId);

public sealed record MovieProductionCheckpointJobSnapshot(
    Guid Id,
    string Status,
    bool HasPublishedOutput);

public sealed record MovieProductionCheckpointItemDto(
    Guid ShotId,
    string SceneTitle,
    int SceneSequence,
    int ShotSequence,
    string Label,
    string State,
    string CurrentStage,
    int ProgressPercent,
    string? BlockedReason,
    string? NextAction,
    Guid? RecoveryJobId,
    Guid? SelectedTakeId,
    DateTime UpdatedAt);

public sealed record MovieProductionRecoveryActionDto(
    Guid ActionId,
    Guid ShotId,
    string SceneTitle,
    string Label,
    string Action,
    string Reason);

public sealed record MovieProductionCheckpointDto(
    Guid MovieProjectId,
    int Version,
    string State,
    int ProgressPercent,
    int TotalShots,
    int CompletedShots,
    int RunningShots,
    int BlockedShots,
    int RecoverableShots,
    int PendingApprovalShots,
    DateTime ObservedAt,
    DateTime? LastRecoveredAt,
    IReadOnlyList<MovieProductionCheckpointItemDto> Items,
    IReadOnlyList<MovieProductionRecoveryActionDto> RecoveryActions);

public sealed class MovieProductionRecoveryRequest
{
    public Guid GenerationJobId { get; set; }
}

public sealed record MovieProductionRecoveryResponse(
    MovieProductionCheckpointDto Checkpoint,
    GenerationJobDto? Job);

public sealed class MovieProductionRecoveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MovieProductionCheckpointProjection(
    IReadOnlyList<MovieProductionCheckpointItemDto> Items,
    IReadOnlyList<MovieProductionRecoveryActionDto> RecoveryActions,
    string State,
    int ProgressPercent,
    int TotalShots,
    int CompletedShots,
    int RunningShots,
    int BlockedShots,
    int RecoverableShots,
    int PendingApprovalShots)
{
    public string SnapshotHash()
    {
        var payload = JsonSerializer.Serialize(new
        {
            State,
            ProgressPercent,
            TotalShots,
            CompletedShots,
            RunningShots,
            BlockedShots,
            RecoverableShots,
            PendingApprovalShots,
            Items,
            RecoveryActions,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

internal static class MovieProductionCheckpointReadiness
{
    public static bool HasPublishableJobOutput(GenerationJob job, MovieProject movie) =>
        job.Assets.Any(asset => IsPublishableAsset(asset, movie))
        || job.Outputs.Any(output => IsPublishableStoredFile(output.StoredFile, movie));

    public static bool IsPublishableAsset(Asset? asset, MovieProject movie) =>
        asset is not null
        && asset.Status == AssetStatus.Active
        && asset.WorkspaceId == movie.WorkspaceId
        && IsProjectScoped(asset.ProjectId, movie.ProjectId)
        && IsPublishableStoredFile(asset.StoredFile, movie);

    public static bool IsPublishableStoredFile(StoredFile? file, MovieProject movie) =>
        file is not null
        && file.Status == StoredFileStatus.Ready
        && file.WorkspaceId == movie.WorkspaceId
        && file.ConversationId is null
        && IsProjectScoped(file.ProjectId, movie.ProjectId);

    private static bool IsProjectScoped(Guid? fileProjectId, Guid? movieProjectId) =>
        !fileProjectId.HasValue || fileProjectId == movieProjectId;
}

public static class MovieProductionCheckpointAnalyzer
{
    public static MovieProductionCheckpointItemDto AnalyzeShot(
        Guid shotId,
        string sceneTitle,
        int sceneSequence,
        int shotSequence,
        string label,
        string shotStatus,
        DateTime updatedAt,
        IReadOnlyList<MovieProductionCheckpointVersionSnapshot> versions,
        IReadOnlyList<MovieProductionCheckpointTakeSnapshot> takes,
        IReadOnlyList<MovieProductionCheckpointJobSnapshot> jobs)
    {
        var jobById = jobs.ToDictionary(item => item.Id);
        var terminalSuccessful = new[] { GenerationJobStatus.Succeeded.ToString(), "Ready", "Completed" };
        var activeJobs = jobs.Where(item => item.Status is "Pending" or "Queued" or "Running").ToArray();
        var failedJob = jobs
            .Where(item => item.Status is "Failed" or "Cancelled")
            .Where(item => !item.HasPublishedOutput)
            .OrderByDescending(item => item.Id)
            .FirstOrDefault();
        var finalTake = takes.FirstOrDefault(item => item.IsFinal && item.HasAsset);
        var selectedTake = takes.FirstOrDefault(item => item.IsSelected && item.HasAsset) ?? takes.FirstOrDefault(item => item.IsSelected);
        var approvedStoryboard = versions.Any(item => item.Stage == MovieProductionStages.ApprovedStoryboard && item.Status == MovieProductionVersionStatuses.Approved);
        var approvedKeyframe = versions.Any(item => item.Stage == MovieProductionStages.ApprovedKeyframe && item.Status == MovieProductionVersionStatuses.Approved);
        var approvedMotion = versions.Any(item => item.Stage == MovieProductionStages.MotionPreview && item.Status == MovieProductionVersionStatuses.Approved);
        var render = versions
            .Where(item => item.Stage == MovieProductionStages.ProductionRender)
            .OrderByDescending(item => item.Id)
            .FirstOrDefault();
        var renderJob = render?.GenerationJobId is Guid renderJobId && jobById.TryGetValue(renderJobId, out var linkedRenderJob) ? linkedRenderJob : null;
        var renderReady = render is not null && renderJob is not null && terminalSuccessful.Contains(renderJob.Status) && (render.HasAsset || renderJob.HasPublishedOutput);
        var pendingVersion = versions
            .Where(item => item.Status == MovieProductionVersionStatuses.PendingApproval && item.Stage != MovieProductionStages.ProductionRender)
            .OrderByDescending(item => item.Id)
            .FirstOrDefault();

        if (finalTake is not null)
            return new MovieProductionCheckpointItemDto(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Complete, MovieProductionStages.SelectedFinalTake, 100, null, null, null, finalTake.Id, updatedAt);
        if (activeJobs.Length > 0)
            return new MovieProductionCheckpointItemDto(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Running, render?.Stage ?? MovieProductionStages.ProductionRender, ProgressFor(approvedStoryboard, approvedKeyframe, approvedMotion, renderReady, selectedTake), null, null, null, selectedTake?.Id, updatedAt);
        if (failedJob is not null)
            return new MovieProductionCheckpointItemDto(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Recoverable, render?.Stage ?? MovieProductionStages.ProductionRender, ProgressFor(approvedStoryboard, approvedKeyframe, approvedMotion, renderReady, selectedTake), "The previous production pass ended before a publishable output was recorded.", MovieProductionCheckpointActions.Retry, failedJob.Id, selectedTake?.Id, updatedAt);
        if (renderReady && takes.All(item => item.GenerationJobId != renderJob?.Id))
            return new MovieProductionCheckpointItemDto(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Blocked, MovieProductionStages.ProductionRender, 90, "The render is ready, but it has not been recorded as a take.", MovieProductionCheckpointActions.CreateTake, null, selectedTake?.Id, updatedAt);
        if (pendingVersion is not null)
        {
            var action = pendingVersion.Stage switch
            {
                MovieProductionStages.StoryboardCandidate => MovieProductionCheckpointActions.ReviewStoryboard,
                MovieProductionStages.ProductionKeyframe => MovieProductionCheckpointActions.ReviewKeyframe,
                MovieProductionStages.MotionPreview => MovieProductionCheckpointActions.ReviewMotionPreview,
                _ => MovieProductionCheckpointActions.ReviewStoryboard,
            };
            return new MovieProductionCheckpointItemDto(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Blocked, pendingVersion.Stage, ProgressFor(approvedStoryboard, approvedKeyframe, approvedMotion, renderReady, selectedTake), "A production review is required before this shot can advance.", action, null, selectedTake?.Id, updatedAt);
        }
        if (!approvedStoryboard)
            return Blocked(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionStages.ShotPlan, 0, "An approved storyboard is required before the shot can advance.", MovieProductionCheckpointActions.CreateStoryboard, selectedTake?.Id, updatedAt);
        if (!approvedKeyframe)
            return Blocked(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionStages.ApprovedStoryboard, 30, "An approved keyframe is required before motion can be reviewed.", MovieProductionCheckpointActions.CreateKeyframe, selectedTake?.Id, updatedAt);
        if (!approvedMotion)
            return Blocked(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionStages.ApprovedKeyframe, 50, "An approved motion preview is required before rendering.", MovieProductionCheckpointActions.CreateMotionPreview, selectedTake?.Id, updatedAt);
        return Blocked(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionStages.MotionPreview, 70, "An approved motion preview is ready for a production render.", MovieProductionCheckpointActions.CreateRender, selectedTake?.Id, updatedAt);
    }

    public static MovieProductionCheckpointProjection Summarize(IEnumerable<MovieProductionCheckpointItemDto> items, IEnumerable<MovieProductionRecoveryActionDto> recoveryActions)
    {
        var orderedItems = items.OrderBy(item => item.SceneSequence).ThenBy(item => item.ShotSequence).ThenBy(item => item.ShotId).ToArray();
        var actions = recoveryActions.OrderBy(item => item.SceneTitle).ThenBy(item => item.Label).ToArray();
        var total = orderedItems.Length;
        var completed = orderedItems.Count(item => item.State == MovieProductionCheckpointStates.Complete);
        var running = orderedItems.Count(item => item.State == MovieProductionCheckpointStates.Running);
        var blocked = orderedItems.Count(item => item.State == MovieProductionCheckpointStates.Blocked);
        var recoverable = orderedItems.Count(item => item.State == MovieProductionCheckpointStates.Recoverable);
        var pending = orderedItems.Count(item => item.NextAction is MovieProductionCheckpointActions.ReviewStoryboard or MovieProductionCheckpointActions.ReviewKeyframe or MovieProductionCheckpointActions.ReviewMotionPreview);
        var state = total == 0 ? MovieProductionCheckpointStates.NotStarted
            : completed == total ? MovieProductionCheckpointStates.Complete
            : running > 0 ? MovieProductionCheckpointStates.Running
            : recoverable > 0 ? MovieProductionCheckpointStates.Recoverable
            : blocked > 0 ? MovieProductionCheckpointStates.Blocked
            : MovieProductionCheckpointStates.Active;
        var progress = total == 0 ? 0 : (int)Math.Round(orderedItems.Average(item => item.ProgressPercent), MidpointRounding.AwayFromZero);
        return new MovieProductionCheckpointProjection(orderedItems, actions, state, Math.Clamp(progress, 0, 100), total, completed, running, blocked, recoverable, pending);
    }

    private static MovieProductionCheckpointItemDto Blocked(Guid shotId, string sceneTitle, int sceneSequence, int shotSequence, string label, string stage, int progress, string reason, string action, Guid? selectedTakeId, DateTime updatedAt) =>
        new(shotId, sceneTitle, sceneSequence, shotSequence, label, MovieProductionCheckpointStates.Blocked, stage, progress, reason, action, null, selectedTakeId, updatedAt);

    private static int ProgressFor(bool approvedStoryboard, bool approvedKeyframe, bool approvedMotion, bool renderReady, MovieProductionCheckpointTakeSnapshot? selectedTake) =>
        selectedTake?.HasAsset == true ? 95 : renderReady ? 90 : approvedMotion ? 70 : approvedKeyframe ? 50 : approvedStoryboard ? 30 : 0;
}
