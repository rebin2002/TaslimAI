using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public sealed class MovieAct
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Status { get; set; } = MovieHierarchyStatuses.Planned;
    public DateTime? StatusChangedAt { get; set; }
    public Guid? StatusChangedByUserId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public ICollection<MovieSequence> Sequences { get; set; } = [];
}

public sealed class MovieSequence
{
    public Guid Id { get; set; }
    public Guid MovieActId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Status { get; set; } = MovieHierarchyStatuses.Planned;
    public DateTime? StatusChangedAt { get; set; }
    public Guid? StatusChangedByUserId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieAct MovieAct { get; set; } = null!;
    public ICollection<MovieScene> Scenes { get; set; } = [];
}

public sealed class MovieTake
{
    public Guid Id { get; set; }
    public Guid MovieShotId { get; set; }
    public int VersionNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = MovieTakeStatuses.Draft;
    public string QualityLevel { get; set; } = MovieQualityLevels.Standard;
    public bool AutoDirectorEnabled { get; set; }
    public Guid? MovieClipId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? MovieProductionVersionId { get; set; }
    public Guid? AssetId { get; set; }
    public string? Notes { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime? SelectedAt { get; set; }
    public Guid? SelectedByUserId { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public Guid? FinalizedByUserId { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public Guid? StatusChangedByUserId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieShot MovieShot { get; set; } = null!;
    public MovieClip? MovieClip { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public MovieProductionVersion? MovieProductionVersion { get; set; }
    public Asset? Asset { get; set; }
    public ICollection<MovieTakeApproval> Approvals { get; set; } = [];
}

public sealed class MovieTakeApproval
{
    public Guid Id { get; set; }
    public Guid MovieTakeId { get; set; }
    public Guid UserId { get; set; }
    public string Decision { get; set; } = MovieApprovalDecisions.Approved;
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public MovieTake MovieTake { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
