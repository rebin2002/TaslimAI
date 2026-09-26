namespace Taslim.Api.Movies;

public static class MovieStoryApprovalStates
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Approved = "Approved";
}

public static class MovieStoryRevisionStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Superseded = "Superseded";
}

public static class MovieStoryAuthorship
{
    public const string Human = "Human";
    public const string AiSuggested = "AiSuggested";
    public const string HumanEdited = "HumanEdited";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Human, AiSuggested, HumanEdited,
    };
}

public static class MovieScreenplayElementTypes
{
    public const string Action = "Action";
    public const string Dialogue = "Dialogue";
    public const string Parenthetical = "Parenthetical";
    public const string Transition = "Transition";
    public const string Note = "Note";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Action, Dialogue, Parenthetical, Transition, Note,
    };
}

public sealed class MovieStory
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Premise { get; set; } = string.Empty;
    public string Logline { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string Treatment { get; set; } = string.Empty;
    public string ApprovalState { get; set; } = MovieStoryApprovalStates.Draft;
    public Guid? CurrentRevisionId { get; set; }
    public Guid? ApprovedRevisionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public ICollection<MovieStoryRevision> Revisions { get; set; } = [];
}

public sealed class MovieStoryRevision
{
    public Guid Id { get; set; }
    public Guid MovieStoryId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public int RevisionNumber { get; set; }
    public string Premise { get; set; } = string.Empty;
    public string Logline { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string Treatment { get; set; } = string.Empty;
    public string Status { get; set; } = MovieStoryRevisionStatuses.Draft;
    public string Authorship { get; set; } = MovieStoryAuthorship.Human;
    public string? ChangeSummary { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }

    public MovieStory MovieStory { get; set; } = null!;
    public ICollection<MovieScreenplayScene> Scenes { get; set; } = [];
}

public sealed class MovieScreenplayScene
{
    public Guid Id { get; set; }
    public Guid MovieStoryRevisionId { get; set; }
    public Guid? MovieSceneId { get; set; }
    public int Ordinal { get; set; }
    public string SceneIdentifier { get; set; } = string.Empty;
    public int? ActNumber { get; set; }
    public int? SequenceNumber { get; set; }
    public string Slugline { get; set; } = string.Empty;
    public string? Synopsis { get; set; }
    public DateTime CreatedAt { get; set; }

    public MovieStoryRevision Revision { get; set; } = null!;
    public MovieScene? MovieScene { get; set; }
    public ICollection<MovieScreenplayElement> Elements { get; set; } = [];
}

public sealed class MovieScreenplayElement
{
    public Guid Id { get; set; }
    public Guid MovieScreenplaySceneId { get; set; }
    public int Ordinal { get; set; }
    public string ElementType { get; set; } = MovieScreenplayElementTypes.Action;
    public string Content { get; set; } = string.Empty;
    public string? CharacterName { get; set; }
    public string? Parenthetical { get; set; }
    public DateTime CreatedAt { get; set; }

    public MovieScreenplayScene Scene { get; set; } = null!;
}
