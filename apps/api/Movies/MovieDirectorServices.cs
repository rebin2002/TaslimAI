using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public interface IMovieDirectorService
{
    Task<DirectorProposalResponse?> CreateProposalAsync(Guid userId, Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken = default);
    Task<DirectorProposalResponse?> GetProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectorHistoryDto>?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
    Task<DirectorProposalDto?> ApproveProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<DirectorProposalDto?> RejectProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<DirectorActionExecutionResponse?> ExecuteActionAsync(Guid userId, Guid actionId, CancellationToken cancellationToken = default);
}

public interface IDirectorActionExecutor
{
    string ActionType { get; }
    Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default);
}

public sealed record DirectorActionExecution(bool Succeeded, string? FailureCode, string SafeMessage, string? ResultJson);

public sealed class MovieDirectorContextAssembler(TaslimDbContext db)
{
    private const int MaxStoryScenes = 40;
    private const int MaxStoryElementsPerScene = 40;
    private const int MaxRelevantReferences = 12;

    public async Task<DirectorContextAssemblyResult?> AssembleAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .Include(item => item.Scenes).ThenInclude(item => item.Shots)
            .Include(item => item.Characters)
            .Include(item => item.Locations)
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        var lockedRevision = movie.Guide.LockedRevisionNumber is int lockedRevisionNumber
            ? movie.Guide.Revisions.FirstOrDefault(item => item.RevisionNumber == lockedRevisionNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        if (lockedRevision is null) return null;
        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var approvedStoryRevision = story?.ApprovedRevisionId is Guid approvedRevisionId
            ? story.Revisions.FirstOrDefault(item => item.Id == approvedRevisionId && item.Status == MovieStoryRevisionStatuses.Approved)
            : null;
        var approvedStoryContext = approvedStoryRevision is null
            ? null
            : new DirectorStoryContext(approvedStoryRevision.Id, approvedStoryRevision.RevisionNumber, approvedStoryRevision.Premise, approvedStoryRevision.Logline, approvedStoryRevision.Synopsis, approvedStoryRevision.Treatment, approvedStoryRevision.Authorship);

        var context = new DirectorContextDto(
            movie.Id, movie.WorkspaceId, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language,
            new DirectorGuideContext(movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, lockedRevision.RevisionNumber, true, lockedRevision.CinematographyBibleJson),
            movie.Scenes.OrderBy(item => item.Sequence).Select(scene => new DirectorSceneContext(
                scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes,
                scene.Shots.OrderBy(item => item.Sequence).Select(shot => new DirectorShotContext(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes)).ToArray())).ToArray(),
            movie.Characters.OrderBy(item => item.CreatedAt).Select(item => new DirectorCharacterContext(item.Name, item.Description, item.Appearance, item.ContinuityNotes)).ToArray(),
            movie.Locations.OrderBy(item => item.CreatedAt).Select(item => new DirectorLocationContext(item.Name, item.Description, item.VisualContinuityNotes)).ToArray(),
            DateTime.UtcNow,
            ApprovedStory: approvedStoryContext);
        var snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        return new DirectorContextAssemblyResult(context, snapshotJson, hash);
    }

    public async Task<(DirectorStoryBoundedContextDto Context, string SnapshotJson, string SnapshotHash)?> AssembleStoryAsync(Guid movieProjectId, Guid? targetSceneId, Guid? targetElementId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .Include(item => item.Scenes).ThenInclude(item => item.Shots)
            .Include(item => item.Characters)
            .Include(item => item.Locations)
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        var lockedRevision = movie.Guide.LockedRevisionNumber is int revisionNumber
            ? movie.Guide.Revisions.FirstOrDefault(item => item.RevisionNumber == revisionNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        if (lockedRevision is null) return null;

        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(item => item.Scenes).ThenInclude(item => item.Elements)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var current = story?.CurrentRevisionId is Guid currentId ? story.Revisions.FirstOrDefault(item => item.Id == currentId) : null;
        var approved = story?.ApprovedRevisionId is Guid approvedId ? story.Revisions.FirstOrDefault(item => item.Id == approvedId && item.Status == MovieStoryRevisionStatuses.Approved) : null;
        var currentContext = current is null ? null : ToStoryRevisionContext(current);
        var approvedContext = approved is null ? null : ToStoryRevisionContext(approved);
        var target = currentContext?.Scenes.FirstOrDefault(item => item.Id == targetSceneId)
            ?? approvedContext?.Scenes.FirstOrDefault(item => item.Id == targetSceneId);
        var movieScenes = movie.Scenes.OrderBy(item => item.Sequence).Take(MaxStoryScenes).Select(scene => new DirectorSceneContext(scene.Id, scene.Sequence, Limit(scene.Title, 160), Limit(scene.Summary, 1_200), scene.DurationSeconds, Limit(scene.ContinuityNotes, 1_200), [])).ToArray();
        var context = new DirectorStoryBoundedContextDto(
            movie.Id,
            movie.WorkspaceId,
            new DirectorGuideContext(Limit(movie.Guide.VisualLanguage, 2_000), Limit(movie.Guide.CameraLanguage, 2_000), Limit(movie.Guide.ColorAndLighting, 2_000), Limit(movie.Guide.SoundAndNarration, 2_000), Limit(movie.Guide.ContinuityRules, 4_000), lockedRevision.RevisionNumber, true, Limit(lockedRevision.CinematographyBibleJson, 8_000)),
            currentContext,
            approvedContext,
            target,
            movieScenes,
            movie.Characters.OrderBy(item => item.CreatedAt).Take(MaxRelevantReferences).Select(item => new DirectorCharacterContext(Limit(item.Name, 160), Limit(item.Description, 1_200), Limit(item.Appearance, 1_200), Limit(item.ContinuityNotes, 1_200))).ToArray(),
            movie.Locations.OrderBy(item => item.CreatedAt).Take(MaxRelevantReferences).Select(item => new DirectorLocationContext(Limit(item.Name, 160), Limit(item.Description, 1_200), Limit(item.VisualContinuityNotes, 1_200))).ToArray(),
            DateTime.UtcNow);
        var snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        return (context, snapshotJson, hash);
    }

    private static DirectorStoryRevisionContext ToStoryRevisionContext(MovieStoryRevision revision) => new(
        revision.Id, revision.RevisionNumber, revision.Status, revision.Authorship,
        Limit(revision.Premise, 8_000), Limit(revision.Logline, 2_000), Limit(revision.Synopsis, 8_000), Limit(revision.Treatment, 8_000),
        revision.Scenes.OrderBy(item => item.Ordinal).Take(MaxStoryScenes).Select(scene => new DirectorScreenplaySceneContext(
            scene.Id, scene.MovieSceneId, Limit(scene.SceneIdentifier, 80), scene.ActNumber, scene.SequenceNumber, Limit(scene.Slugline, 500), Limit(scene.Synopsis, 2_000),
            scene.Elements.OrderBy(item => item.Ordinal).Take(MaxStoryElementsPerScene).Select(element => new DirectorScreenplayElementContext(element.Id, element.ElementType, Limit(element.Content, 6_000), Limit(element.CharacterName, 160), Limit(element.Parenthetical, 500))).ToArray())).ToArray());

    private static string Limit(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];
}

public sealed class MovieDirectorService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    MovieDirectorContextAssembler assembler,
    DirectorQualityPlanner qualityPlanner,
    DirectorStoryProposalPlanner storyPlanner,
    IEnumerable<IDirectorActionExecutor> executors) : IMovieDirectorService
{
    public async Task<DirectorProposalResponse?> CreateProposalAsync(Guid userId, Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (!string.IsNullOrWhiteSpace(request.StoryAction))
            return await CreateStoryProposalAsync(userId, movie, request, cancellationToken);
        var context = await assembler.AssembleAsync(userId, movieProjectId, cancellationToken);
        if (context is null) return null;
        var shot = request.ShotId.HasValue
            ? context.Context.Scenes.SelectMany(item => item.Shots).FirstOrDefault(item => item.Id == request.ShotId.Value)
            : context.Context.Scenes.SelectMany(item => item.Shots).FirstOrDefault();
        if (shot is null) throw new DirectorValidationException("Select a shot before creating a Director proposal.");
        if (request.BudgetLimitUsd is < 0) throw new DirectorValidationException("Budget limit cannot be negative.");

        var recommendation = qualityPlanner.Recommend(new DirectorShotPlanningRequest(
            shot.Id, request.Importance, request.Complexity, request.BudgetSensitivity,
            shot.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), context.Context.Guide.ContinuityRules.Length > 0,
            request.RequestedQuality, request.BudgetLimitUsd));
        var rationale = recommendation.Reasons.ToArray();
        var now = DateTime.UtcNow;
        var directorContext = await GetOrCreateContextAsync(movie, context, cancellationToken);
        var proposal = new DirectorProposal
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProjectContextId = directorContext.Id,
            CreatedByUserId = userId, Status = DirectorProposalStatuses.PendingApproval,
            Title = string.IsNullOrWhiteSpace(request.Goal) ? $"Plan shot {shot.Sequence}" : request.Goal.Trim(),
            Summary = $"Prepare shot {shot.Sequence} at {recommendation.QualityLevel} quality.",
            RationaleJson = JsonSerializer.Serialize(rationale), CreatedAt = now,
        };
        var action = new DirectorAction
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProposalId = proposal.Id,
            ActionType = DirectorActionTypes.GenerateShot, Status = DirectorActionStatuses.PendingApproval, ApprovalRequired = true,
            PayloadJson = JsonSerializer.Serialize(new DirectorGenerateShotPayload(shot.Id, recommendation.QualityLevel, shot.DurationSeconds ?? 60, recommendation.EstimatedCostUsd)), CreatedAt = now,
        };
        proposal.Actions.Add(action);
        db.DirectorProposals.Add(proposal);
        db.DirectorDecisions.Add(new DirectorDecision
        {
            Id = Guid.NewGuid(), DirectorProjectContextId = directorContext.Id, MovieShotId = shot.Id, DecisionType = "quality_recommendation",
            QualityLevel = recommendation.QualityLevel, RationaleJson = JsonSerializer.Serialize(rationale), EstimatedCostUsd = recommendation.EstimatedCostUsd, CreatedAt = now,
        });
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ContextAssembled,
            SafeDetailsJson = JsonSerializer.Serialize(new { contextVersion = context.Context.ContextVersion, snapshotHash = context.SnapshotHash }), CreatedAt = now,
        });
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ProposalCreated,
            SafeDetailsJson = JsonSerializer.Serialize(new { qualityLevel = recommendation.QualityLevel, estimatedCostUsd = recommendation.EstimatedCostUsd }), CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new DirectorProposalResponse(ToDto(proposal, rationale, [new DirectorPlanItemDto(shot.Id, shot.Sequence, shot.Description, recommendation)]), context.Context);
    }

    public async Task<DirectorProposalResponse?> GetProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await QueryProposal().FirstOrDefaultAsync(item => item.Id == proposalId, cancellationToken);
        if (proposal is null || !await access.IsMemberAsync(userId, proposal.WorkspaceId, cancellationToken)) return null;
        var context = await assembler.AssembleAsync(userId, proposal.MovieProjectId, cancellationToken);
        if (context is null) return null;
        var storyAction = proposal.Actions.FirstOrDefault(item => item.ActionType == DirectorActionTypes.StoryAssistance);
        var storyContext = storyAction is null ? null : await assembler.AssembleStoryAsync(proposal.MovieProjectId, ReadStoryPayload(storyAction)?.TargetSceneId, ReadStoryPayload(storyAction)?.TargetElementId, cancellationToken);
        return new DirectorProposalResponse(ToDto(proposal), context.Context, storyContext?.Context);
    }

    public async Task<IReadOnlyList<DirectorHistoryDto>?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        return await db.DirectorHistoryEvents.AsNoTracking().Where(item => item.WorkspaceId == movie.WorkspaceId && (item.Proposal == null || item.Proposal.MovieProjectId == movieProjectId))
            .OrderByDescending(item => item.CreatedAt).Take(200).Select(item => new DirectorHistoryDto(item.Id, item.EventType, item.SafeDetailsJson, item.CreatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<DirectorProposalDto?> ApproveProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default) => await SetProposalStatusAsync(userId, proposalId, true, cancellationToken);
    public async Task<DirectorProposalDto?> RejectProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default) => await SetProposalStatusAsync(userId, proposalId, false, cancellationToken);

    public async Task<DirectorActionExecutionResponse?> ExecuteActionAsync(Guid userId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await db.DirectorActions.Include(item => item.Proposal).Include(item => item.Results).FirstOrDefaultAsync(item => item.Id == actionId, cancellationToken);
        if (action is null || !await access.IsMemberAsync(userId, action.WorkspaceId, cancellationToken)) return null;
        if (action.Status != DirectorActionStatuses.Ready) throw new DirectorActionNotApprovedException();
        var executor = executors.FirstOrDefault(item => string.Equals(item.ActionType, action.ActionType, StringComparison.OrdinalIgnoreCase));
        if (executor is null) throw new DirectorActionExecutionException("DIRECTOR_ACTION_UNSUPPORTED", "This Director action is not available.");
        action.Status = DirectorActionStatuses.Running;
        action.StartedAt = DateTime.UtcNow;
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = action.WorkspaceId, DirectorActionId = action.Id, DirectorProposalId = action.DirectorProposalId, EventType = DirectorHistoryEventTypes.ActionStarted, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);

        DirectorActionExecution execution;
        try { execution = await executor.ExecuteAsync(action, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { execution = new(false, "DIRECTOR_ACTION_FAILED", "The Director action could not be completed.", null); }
        var now = DateTime.UtcNow;
        action.Status = execution.Succeeded ? DirectorActionStatuses.Succeeded : DirectorActionStatuses.Failed;
        action.FailureCode = execution.FailureCode;
        action.CompletedAt = now;
        var result = new DirectorActionResult { Id = Guid.NewGuid(), DirectorActionId = action.Id, Status = action.Status, SafeMessage = execution.SafeMessage, ResultJson = execution.ResultJson, CreatedAt = now };
        db.DirectorActionResults.Add(result);
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = action.WorkspaceId, DirectorActionId = action.Id, DirectorProposalId = action.DirectorProposalId, EventType = execution.Succeeded ? DirectorHistoryEventTypes.ActionSucceeded : DirectorHistoryEventTypes.ActionFailed, SafeDetailsJson = JsonSerializer.Serialize(new { execution.FailureCode }), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return new DirectorActionExecutionResponse(ToDto(action), ToDto(result));
    }

    private async Task<DirectorProposalDto?> SetProposalStatusAsync(Guid userId, Guid proposalId, bool approve, CancellationToken cancellationToken)
    {
        var proposal = await db.DirectorProposals.Include(item => item.Actions).Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == proposalId, cancellationToken);
        if (proposal is null || !await access.IsMemberAsync(userId, proposal.WorkspaceId, cancellationToken)) return null;
        if (proposal.Status != DirectorProposalStatuses.PendingApproval) throw new DirectorValidationException("This proposal is no longer awaiting approval.");
        var now = DateTime.UtcNow;
        proposal.Status = approve ? DirectorProposalStatuses.Approved : DirectorProposalStatuses.Rejected;
        proposal.ApprovedAt = approve ? now : null;
        proposal.RejectedAt = approve ? null : now;
        foreach (var action in proposal.Actions) { action.Status = approve ? DirectorActionStatuses.Ready : DirectorActionStatuses.Cancelled; action.ApprovedAt = approve ? now : null; }
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = proposal.WorkspaceId, DirectorProposalId = proposal.Id, EventType = approve ? DirectorHistoryEventTypes.ProposalApproved : DirectorHistoryEventTypes.ProposalRejected, CreatedAt = now });
        if (approve) foreach (var action in proposal.Actions) db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = proposal.WorkspaceId, DirectorProposalId = proposal.Id, DirectorActionId = action.Id, EventType = DirectorHistoryEventTypes.ActionReady, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(proposal);
    }

    private async Task<DirectorProposalResponse?> CreateStoryProposalAsync(Guid userId, MovieProject movie, DirectorProposalRequest request, CancellationToken cancellationToken)
    {
        var assembled = await assembler.AssembleAsync(userId, movie.Id, cancellationToken);
        var storyContext = await assembler.AssembleStoryAsync(movie.Id, request.TargetSceneId, request.TargetElementId, cancellationToken);
        if (assembled is null || storyContext is null) return null;
        var plan = storyPlanner.Build(request, storyContext.Value.Context);
        var now = DateTime.UtcNow;
        var directorContext = await GetOrCreateContextAsync(movie, assembled, cancellationToken);
        var proposal = new DirectorProposal
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProjectContextId = directorContext.Id,
            CreatedByUserId = userId, Status = DirectorProposalStatuses.PendingApproval, Title = plan.Title, Summary = plan.Summary,
            RationaleJson = JsonSerializer.Serialize(plan.Rationale), CreatedAt = now,
        };
        proposal.Actions.Add(new DirectorAction
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProposalId = proposal.Id,
            ActionType = DirectorActionTypes.StoryAssistance, Status = DirectorActionStatuses.PendingApproval, ApprovalRequired = true,
            PayloadJson = JsonSerializer.Serialize(plan.Payload, DirectorJson.Options), CreatedAt = now,
        });
        db.DirectorProposals.Add(proposal);
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ContextAssembled, SafeDetailsJson = JsonSerializer.Serialize(new { contextVersion = storyContext.Value.Context.ContextVersion, snapshotHash = storyContext.Value.SnapshotHash }), CreatedAt = now });
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ProposalCreated, SafeDetailsJson = JsonSerializer.Serialize(new { action = plan.Payload.Action, appliesToStory = plan.Review.AppliesToStory }), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return new DirectorProposalResponse(ToDto(proposal, plan.Rationale, [], plan.Review), assembled.Context, storyContext.Value.Context);
    }

    private async Task<DirectorProjectContext> GetOrCreateContextAsync(MovieProject movie, DirectorContextAssemblyResult assembled, CancellationToken cancellationToken)
    {
        var existing = await db.DirectorProjectContexts.FirstOrDefaultAsync(item => item.MovieProjectId == movie.Id, cancellationToken);
        if (existing is null)
        {
            existing = new DirectorProjectContext { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, SnapshotJson = assembled.SnapshotJson, SnapshotHash = assembled.SnapshotHash, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.DirectorProjectContexts.Add(existing);
        }
        else { existing.ContextVersion++; existing.SnapshotJson = assembled.SnapshotJson; existing.SnapshotHash = assembled.SnapshotHash; existing.UpdatedAt = DateTime.UtcNow; }
        return existing;
    }

    private IQueryable<DirectorProposal> QueryProposal() => db.DirectorProposals.AsNoTracking().Include(item => item.Actions).ThenInclude(item => item.Results).Include(item => item.MovieProject);

    private static DirectorProposalDto ToDto(DirectorProposal proposal, IReadOnlyList<string>? rationale = null, IReadOnlyList<DirectorPlanItemDto>? plan = null, DirectorStoryReviewDto? storyReview = null) =>
        new(proposal.Id, proposal.MovieProjectId, proposal.Status, proposal.Title, proposal.Summary, rationale ?? ParseRationale(proposal.RationaleJson), plan ?? [], proposal.Actions.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(), proposal.CreatedAt, proposal.ApprovedAt, storyReview ?? proposal.Actions.Select(ReadStoryReview).FirstOrDefault(item => item is not null));
    private static DirectorActionDto ToDto(DirectorAction action) => new(action.Id, action.DirectorProposalId, action.ActionType, action.Status, action.ApprovalRequired, action.FailureCode, action.CreatedAt, action.ApprovedAt, action.StartedAt, action.CompletedAt, action.Results.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray());
    private static DirectorActionResultDto ToDto(DirectorActionResult result) => new(result.Id, result.Status, result.SafeMessage, result.ResultJson, result.CreatedAt);
    private static IReadOnlyList<string> ParseRationale(string json) { try { return JsonSerializer.Deserialize<string[]>(json) ?? []; } catch (JsonException) { return []; } }
    private static DirectorStoryActionPayload? ReadStoryPayload(DirectorAction action) { try { return JsonSerializer.Deserialize<DirectorStoryActionPayload>(action.PayloadJson, DirectorJson.Options); } catch (JsonException) { return null; } }
    private static DirectorStoryReviewDto? ReadStoryReview(DirectorAction action)
    {
        if (!string.Equals(action.ActionType, DirectorActionTypes.StoryAssistance, StringComparison.OrdinalIgnoreCase)) return null;
        var payload = ReadStoryPayload(action);
        return payload is null ? null : new DirectorStoryReviewDto(payload.Action, payload.BaseRevisionId, payload.Changes, payload.Findings, !string.Equals(payload.Action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class MovieDirectorActionExecutor(IMovieStudioService movies, IMovieVideoProvider provider) : IDirectorActionExecutor
{
    public string ActionType => DirectorActionTypes.GenerateShot;
    public async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default)
    {
        if (!provider.IsAvailable) return new(false, GenerationJobErrorCodes.MovieProviderUnavailable, "The movie generation capability is not available.", null);
        DirectorGenerateShotPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorGenerateShotPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null) return new(false, "DIRECTOR_ACTION_INVALID", "The Director action payload is invalid.", null);
        var result = await movies.GenerateShotAsync(action.Proposal.CreatedByUserId, payload.ShotId, new MovieStudioGenerationRequest($"Director: {payload.QualityLevel}", payload.EstimatedCostUsd), cancellationToken, action.IdempotencyKey ?? $"director:{action.Id:N}");
        return result is null
            ? new(false, "DIRECTOR_SHOT_NOT_FOUND", "The Director shot could not be found.", null)
            : new(true, null, "The Director queued the shot for generation.", JsonSerializer.Serialize(new { result.Job.Id, result.ClipId }));
    }
}
