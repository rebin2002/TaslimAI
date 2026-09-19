using System.ComponentModel.DataAnnotations;

namespace Taslim.Api.Contracts;

public sealed record ConversationDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string Title,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastMessageAt);

public sealed record ChatMessageDto(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    string Status,
    DateTime CreatedAt,
    long Sequence,
    bool IsTestResponse = false);

public sealed class CreateConversationRequest
{
    [StringLength(160)]
    public string? Title { get; set; }

    public Guid? ProjectId { get; set; }
}

public sealed class RenameConversationRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;
}

public sealed class SendMessageRequest
{
    [Required, StringLength(20_000, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;

    [StringLength(80)]
    public string? RequestId { get; set; }
}

public sealed record SendMessageResponse(
    ConversationDto Conversation,
    ChatMessageDto UserMessage,
    ChatMessageDto AssistantMessage);

public sealed record ChatStreamEvent(string Type, object? Data = null);

public static class ChatMessageLimits
{
    public const int MaximumContentLength = 20_000;
}
