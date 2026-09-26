using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieTeamRoles
{
    public const string Producer = "Producer";
    public const string Director = "Director";
    public const string Writer = "Writer";
    public const string ArtDirector = "ArtDirector";
    public const string Cinematographer = "Cinematographer";
    public const string Editor = "Editor";
    public const string SoundMusic = "SoundMusic";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Producer, Director, Writer, ArtDirector, Cinematographer, Editor, SoundMusic,
    };
}

public static class MoviePermissions
{
    public const string View = "View";
    public const string Comment = "Comment";
    public const string Edit = "Edit";
    public const string Generate = "Generate";
    public const string Approve = "Approve";
    public const string ManageBudget = "ManageBudget";
    public const string ManageTeam = "ManageTeam";
    public const string FinalApproval = "FinalApproval";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        View, Comment, Edit, Generate, Approve, ManageBudget, ManageTeam, FinalApproval,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Defaults =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [MovieTeamRoles.Producer] = Supported,
            [MovieTeamRoles.Director] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit, Generate, Approve },
            [MovieTeamRoles.Writer] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit },
            [MovieTeamRoles.ArtDirector] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit, Generate, Approve },
            [MovieTeamRoles.Cinematographer] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit, Generate },
            [MovieTeamRoles.Editor] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit, Generate, Approve },
            [MovieTeamRoles.SoundMusic] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { View, Comment, Edit, Generate },
        };

    public static IReadOnlySet<string> ForRole(string role) =>
        Defaults.TryGetValue(role, out var permissions) ? permissions : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public static class MovieCollaborationTargetTypes
{
    public const string Project = "project";
    public const string Guide = "guide";
    public const string Scene = "scene";
    public const string Character = "character";
    public const string Location = "location";
    public const string Shot = "shot";
    public const string Clip = "clip";
    public const string Assembly = "assembly";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Project, Guide, Scene, Character, Location, Shot, Clip, Assembly,
    };
}

public static class MovieReviewStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string ChangesRequested = "ChangesRequested";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlySet<string> Decisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Approved, ChangesRequested, Rejected,
    };
}

public static class MovieAssignmentStatuses
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Blocked = "Blocked";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Open, InProgress, Blocked, Completed, Cancelled,
    };
}

public sealed class MovieTeamMember
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = MovieTeamRoles.Writer;
    public bool IsProjectOwner { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
    public ICollection<MovieTeamMemberPermission> PermissionOverrides { get; set; } = [];
}

public sealed class MovieTeamMemberPermission
{
    public Guid Id { get; set; }
    public Guid MovieTeamMemberId { get; set; }
    public string Permission { get; set; } = string.Empty;
    public bool Granted { get; set; }
    public MovieTeamMember MovieTeamMember { get; set; } = null!;
}

public sealed class MovieComment
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string TargetType { get; set; } = MovieCollaborationTargetTypes.Project;
    public Guid TargetId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? ParentCommentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser AuthorUser { get; set; } = null!;
    public MovieComment? ParentComment { get; set; }
    public ICollection<MovieCommentMention> Mentions { get; set; } = [];
}

public sealed class MovieCommentMention
{
    public Guid Id { get; set; }
    public Guid CommentId { get; set; }
    public Guid MentionedUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieComment Comment { get; set; } = null!;
    public ApplicationUser MentionedUser { get; set; } = null!;
}

public sealed class MovieReview
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string TargetType { get; set; } = MovieCollaborationTargetTypes.Project;
    public Guid TargetId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid ReviewerUserId { get; set; }
    public bool IsFinal { get; set; }
    public string Status { get; set; } = MovieReviewStatuses.Pending;
    public string? RequestNote { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser RequestedByUser { get; set; } = null!;
    public ApplicationUser ReviewerUser { get; set; } = null!;
}

public sealed class MovieProductionAssignment
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid AssigneeUserId { get; set; }
    public Guid AssignedByUserId { get; set; }
    public string TargetType { get; set; } = MovieCollaborationTargetTypes.Project;
    public Guid TargetId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = MovieAssignmentStatuses.Open;
    public DateTime? DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser AssigneeUser { get; set; } = null!;
    public ApplicationUser AssignedByUser { get; set; } = null!;
}

public sealed class MovieProductionCredit
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = MovieTeamRoles.Producer;
    public string? CreditName { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
