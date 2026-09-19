using Microsoft.AspNetCore.Identity;

namespace Taslim.Api.Domain;

public static class LanguageCodes
{
    public const string English = "en";
    public const string Arabic = "ar";
    public const string Kurdish = "ku";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        English, Arabic, Kurdish,
    };
}

public enum WorkspaceType
{
    Personal,
    Business,
}

public enum WorkspaceRole
{
    Owner,
    Admin,
    Member,
}

public static class ProjectTypes
{
    public const string General = "General";
    public const string Movie = "Movie";
    public const string Marketing = "Marketing";
    public const string Business = "Business";
    public const string Research = "Research";
    public const string Education = "Education";
    public const string Development = "Development";

    public static readonly IReadOnlySet<string> Initial = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        General, Movie, Marketing, Business, Research, Education, Development,
    };
}

public static class ProjectStatuses
{
    public const string Active = "Active";
    public const string Archived = "Archived";
}

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = LanguageCodes.English;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<WorkspaceMember> WorkspaceMemberships { get; set; } = [];
}

public sealed class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public WorkspaceType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsArchived { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
    public ICollection<Conversation> Conversations { get; set; } = [];
}

public sealed class WorkspaceMember
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public DateTime JoinedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}

public sealed class Project
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = ProjectTypes.General;
    public string Status { get; set; } = ProjectStatuses.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}

public enum ConversationStatus
{
    Active,
    Archived,
}

public enum ChatMessageRole
{
    User,
    Assistant,
    System,
    Tool,
}

public enum ChatMessageStatus
{
    Pending,
    Completed,
    Failed,
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ICollection<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public ChatMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.Pending;
    public DateTime CreatedAt { get; set; }

    // Reserved for future attachment manifests stored outside PostgreSQL.
    public string? AttachmentManifestJson { get; set; }
    public string? ProviderKey { get; set; }
    public string? ModelKey { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public int? LatencyMs { get; set; }
    public string? FinishReason { get; set; }

    public Conversation Conversation { get; set; } = null!;
}

public sealed record AiProviderDefinition(string Key, string Name, bool Enabled);

public sealed record AiModelDefinition(
    string ProviderKey,
    string ModelKey,
    string Name,
    string Capabilities,
    int ContextWindow,
    bool SupportsStreaming,
    bool SupportsVision,
    bool SupportsTools,
    string CostTier);
