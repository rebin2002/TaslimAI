namespace Taslim.Api.Movies;

/// <summary>
/// Stable operation names shared by Movie services and the UI capability contract.
/// These names map to the established MoviePermissions; they are not a second
/// permission vocabulary.
/// </summary>
public static class MovieOperationalActions
{
    public const string StoryEdit = "story.edit";
    public const string StoryApproval = "story.approve";
    public const string GuideEdit = "guide.edit";
    public const string GuideApproval = "guide.approve";
    public const string CastEdit = "cast.edit";
    public const string WorldEdit = "world.edit";
    public const string SceneEdit = "scene.edit";
    public const string ShotEdit = "shot.edit";
    public const string ProductionVersionEdit = "production.version.edit";
    public const string Generate = "generate";
    public const string RenderTake = "render.take";
    public const string StoryboardApproval = "storyboard.approve";
    public const string KeyframeApproval = "keyframe.approve";
    public const string ProductionReview = "production.review";
    public const string TakeCreate = "take.create";
    public const string TakeSelect = "take.select";
    public const string TakeApproval = "take.approve";
    public const string TakeFinalization = "take.finalize";
    public const string DirectorProposalCreate = "director.proposal.create";
    public const string DirectorProposalApproval = "director.proposal.approve";
    public const string DirectorProposalExecution = "director.proposal.execute";
    public const string Comments = "comments";
    public const string ReviewsRequest = "reviews.request";
    public const string ReviewsDecision = "reviews.decide";
    public const string FinalReviewDecision = "reviews.final.decide";
    public const string TeamManagement = "team.manage";
    public const string BudgetManagement = "budget.manage";

}

/// <summary>
/// The only translation layer between operational actions and the established
/// Movie permission system.
/// </summary>
public static class MovieOperationalPolicies
{
    private static readonly IReadOnlyDictionary<string, string> RequiredPermissions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MovieOperationalActions.StoryEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.StoryApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.GuideEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.GuideApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.CastEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.WorldEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.SceneEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.ShotEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.ProductionVersionEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.Generate] = MoviePermissions.Generate,
            [MovieOperationalActions.RenderTake] = MoviePermissions.Generate,
            [MovieOperationalActions.StoryboardApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.KeyframeApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.ProductionReview] = MoviePermissions.Approve,
            [MovieOperationalActions.TakeCreate] = MoviePermissions.Edit,
            [MovieOperationalActions.TakeSelect] = MoviePermissions.Edit,
            [MovieOperationalActions.TakeApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.TakeFinalization] = MoviePermissions.FinalApproval,
            [MovieOperationalActions.DirectorProposalCreate] = MoviePermissions.Generate,
            [MovieOperationalActions.DirectorProposalApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.DirectorProposalExecution] = MoviePermissions.Generate,
            [MovieOperationalActions.Comments] = MoviePermissions.Comment,
            [MovieOperationalActions.ReviewsRequest] = MoviePermissions.Comment,
            [MovieOperationalActions.ReviewsDecision] = MoviePermissions.Approve,
            [MovieOperationalActions.FinalReviewDecision] = MoviePermissions.FinalApproval,
            [MovieOperationalActions.TeamManagement] = MoviePermissions.ManageTeam,
            [MovieOperationalActions.BudgetManagement] = MoviePermissions.ManageBudget,
        };

    public static string RequiredPermission(string action) =>
        RequiredPermissions.TryGetValue(action, out var permission)
            ? permission
            : throw new ArgumentException("Unsupported Movie operational action.", nameof(action));

    public static IReadOnlyDictionary<string, string> All() => RequiredPermissions;
}

public sealed record MovieCapabilityResponse(
    Guid MovieProjectId,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, bool> Capabilities);

/// <summary>
/// Server-authoritative Movie authorization facade. Services use operation names
/// here, while the facade evaluates only MovieCollaborationAccess and its existing
/// roles/overrides.
/// </summary>
public sealed class MovieAuthorizationService(MovieCollaborationAccess collaboration)
{
    public Task<bool> CanAsync(Guid userId, Guid movieProjectId, string action, CancellationToken cancellationToken) =>
        collaboration.HasPermissionAsync(userId, movieProjectId, MovieOperationalPolicies.RequiredPermission(action), cancellationToken);

    public Task<bool> CanPermissionAsync(Guid userId, Guid movieProjectId, string permission, CancellationToken cancellationToken) =>
        collaboration.HasPermissionAsync(userId, movieProjectId, permission, cancellationToken);

    public Task RequireAsync(Guid userId, Guid movieProjectId, string action, CancellationToken cancellationToken) =>
        collaboration.RequireAsync(userId, movieProjectId, MovieOperationalPolicies.RequiredPermission(action), cancellationToken);

    public async Task<MovieCapabilityResponse?> GetCapabilitiesAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var member = await collaboration.GetMemberAsync(userId, movieProjectId, cancellationToken);
        if (member is null) return null;

        var permissions = MovieCollaborationAccess.EffectivePermissions(member);
        var capabilities = MovieOperationalPolicies.All()
            .Keys
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(action => action, action => permissions.Contains(MovieOperationalPolicies.RequiredPermission(action), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        return new MovieCapabilityResponse(movieProjectId, permissions, capabilities);
    }
}
