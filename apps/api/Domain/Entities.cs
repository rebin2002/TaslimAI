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

public static class GenerationJobTypes
{
    public const string SystemTest = "system.test";
    public const string ImageGenerate = "image.generate";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SystemTest,
        ImageGenerate,
    };
}

public enum GenerationJobStatus
{
    Pending,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public static class GenerationJobOutputTypes
{
    public const string StoredFile = "stored_file";
    public const string Json = "json";
}

public static class GenerationJobErrorCodes
{
    public const string TypeNotSupported = "JOB_TYPE_NOT_SUPPORTED";
    public const string Cancelled = "JOB_CANCELLED";
    public const string ExecutionFailed = "JOB_EXECUTION_FAILED";
    public const string NotCancellable = "JOB_NOT_CANCELLABLE";
    public const string NotFound = "JOB_NOT_FOUND";
    public const string ImageRequestInvalid = "IMAGE_REQUEST_INVALID";
    public const string ImageProviderUnavailable = "IMAGE_PROVIDER_UNAVAILABLE";
    public const string ImageGenerationFailed = "IMAGE_GENERATION_FAILED";
    public const string ImageOutputInvalid = "IMAGE_OUTPUT_INVALID";
    public const string ImageOutputStorageFailed = "IMAGE_OUTPUT_STORAGE_FAILED";
    public const string ImageCancelled = "IMAGE_CANCELLED";
    public const string ImageSafetyRefusal = "IMAGE_SAFETY_REFUSAL";
    public const string ImageReferenceNotSupported = "IMAGE_REFERENCE_NOT_SUPPORTED";
}

public static class AssetTypes
{
    public const string Image = "image";
    public const string Document = "document";
    public const string Presentation = "presentation";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Music = "music";
    public const string Research = "research";
    public const string Social = "social";
    public const string File = "file";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Image, Document, Presentation, Video, Audio, Music, Research, Social, File, Other,
    };
}

public enum AssetStatus
{
    Active,
    Archived,
}

public static class PersonalMemoryCategories
{
    public const string Preference = "Preference";
    public const string Personal = "Personal";
    public const string Business = "Business";
    public const string Writing = "Writing";
    public const string Language = "Language";
    public const string Other = "Other";

    public static readonly IReadOnlySet<string> Initial = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Preference, Personal, Business, Writing, Language, Other,
    };
}

public static class PersonalMemorySources
{
    public const string Manual = "Manual";
}

public enum StoredFileStatus
{
    Uploading,
    Ready,
    Processing,
    Failed,
    Deleted,
}

public enum FileExtractionStatus
{
    NotStarted,
    Processing,
    Ready,
    Failed,
    NotApplicable,
}

public static class FileStorageProviders
{
    public const string Local = "Local";
    public const string S3Compatible = "S3Compatible";
}

public static class FileContentTypes
{
    public static readonly IReadOnlyDictionary<string, string> AllowedExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    };

    public static bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".webp";
    public static bool IsTextExtractable(string extension) => extension is ".pdf" or ".docx" or ".txt" or ".md" or ".csv" or ".xlsx";
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
    public ICollection<StoredFile> Files { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
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
    public string? Instructions { get; set; }
    public string? ContextNotes { get; set; }
    public string Type { get; set; } = ProjectTypes.General;
    public string Status { get; set; } = ProjectStatuses.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public ICollection<StoredFile> Files { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
}

public sealed class PersonalMemory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Category { get; set; } = PersonalMemoryCategories.Other;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = PersonalMemorySources.Manual;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ApplicationUser User { get; set; } = null!;
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

public enum UsageFeature
{
    Chat,
    Generation,
    Image,
    Movie,
    Voice,
    Music,
    Document,
    Research,
    Presentation,
}

public enum UsageTransactionStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
    Refunded,
}

public enum UsageChargeUnit
{
    Usd,
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
    public long NextMessageSequence { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ICollection<ChatMessage> Messages { get; set; } = [];
    public ICollection<StoredFile> Files { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public ChatMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public long Sequence { get; set; }
    public string? RequestId { get; set; }

    // Reserved for future attachment manifests stored outside PostgreSQL.
    public string? AttachmentManifestJson { get; set; }
    public string? ProviderKey { get; set; }
    public string? ModelKey { get; set; }
    public int? InputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public int? LatencyMs { get; set; }
    public string? FinishReason { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public ICollection<ChatMessageAttachment> Attachments { get; set; } = [];
}

public sealed class StoredFile
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ConversationId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageProvider { get; set; } = FileStorageProviders.Local;
    public string StorageKey { get; set; } = string.Empty;
    public StoredFileStatus Status { get; set; } = StoredFileStatus.Uploading;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public FileExtractionStatus TextExtractionStatus { get; set; } = FileExtractionStatus.NotStarted;
    public string? ExtractedText { get; set; }
    public int? ExtractedTextLength { get; set; }
    public string? MetadataJson { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
    public Project? Project { get; set; }
    public Conversation? Conversation { get; set; }
    public ICollection<ChatMessageAttachment> Attachments { get; set; } = [];
    public ICollection<GenerationJobOutput> GenerationJobOutputs { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
}

public sealed class GenerationJob
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public GenerationJobStatus Status { get; set; } = GenerationJobStatus.Pending;
    public string? Title { get; set; }
    public string? Provider { get; set; }
    public string? ProviderModel { get; set; }
    public string InputJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int ProgressPercent { get; set; }
    public bool CancellationRequested { get; set; }
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? ClaimExpiresAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ICollection<GenerationJobOutput> Outputs { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
}

public sealed class Asset
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? StoredFileId { get; set; }
    public Guid? SourceGenerationJobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string AssetType { get; set; } = AssetTypes.File;
    public string? MimeType { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public StoredFile? StoredFile { get; set; }
    public GenerationJob? SourceGenerationJob { get; set; }
}

public sealed class GenerationJobOutput
{
    public Guid Id { get; set; }
    public Guid GenerationJobId { get; set; }
    public Guid? StoredFileId { get; set; }
    public string OutputType { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }

    public GenerationJob GenerationJob { get; set; } = null!;
    public StoredFile? StoredFile { get; set; }
}

public sealed class ChatMessageAttachment
{
    public Guid ChatMessageId { get; set; }
    public Guid StoredFileId { get; set; }
    public int SortOrder { get; set; }

    public ChatMessage ChatMessage { get; set; } = null!;
    public StoredFile StoredFile { get; set; } = null!;
}

public sealed class UsageTransaction
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public UsageFeature Feature { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public UsageTransactionStatus Status { get; set; } = UsageTransactionStatus.Pending;
    public int? InputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ImageInputTokens { get; set; }
    public int? ImageOutputTokens { get; set; }
    public int? LatencyMs { get; set; }
    public decimal? EstimatedProviderCostUsd { get; set; }
    public decimal ProviderCostUsd { get; set; }
    public decimal ChargedAmount { get; set; }
    public UsageChargeUnit ChargedUnit { get; set; } = UsageChargeUnit.Usd;
    public string Currency { get; set; } = "USD";
    public string? PricingVersion { get; set; }
    public string? PricingSnapshotJson { get; set; }
    public string? SafeMetadataJson { get; set; }
    public bool IsAnomalous { get; set; }
    public string? AnomalyCode { get; set; }
    public DateTime? AnomalyDetectedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? RefundedAt { get; set; }
    public string? FailureCode { get; set; }

    public GenerationJob? GenerationJob { get; set; }
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
