using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class ChatController(
    TaslimDbContext db,
    WorkspaceAccessService access,
    IChatCompletionService completion,
    ILogger<ChatController> logger) : ControllerBase
{
    [HttpPost("workspaces/{workspaceId:guid}/conversations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateConversation(Guid workspaceId, CreateConversationRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();

        if (request.ProjectId is not null)
        {
            var belongsToWorkspace = await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == workspaceId, cancellationToken);
            if (!belongsToWorkspace) return ApiResults.Error(this, StatusCodes.Status404NotFound, "PROJECT_NOT_FOUND", "Project not found.");
        }

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ProjectId = request.ProjectId,
            UserId = userId,
            Title = NormalizeTitle(request.Title) ?? "New chat",
            Status = ConversationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetConversation), new { conversationId = conversation.Id }, ToDto(conversation));
    }

    [HttpGet("workspaces/{workspaceId:guid}/conversations")]
    public async Task<IActionResult> ListConversations(Guid workspaceId, [FromQuery] string? status, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        var requestedStatus = string.Equals(status, ConversationStatus.Archived.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ConversationStatus.Archived : ConversationStatus.Active;
        var conversations = await db.Conversations.AsNoTracking()
            .Where(conversation => conversation.WorkspaceId == workspaceId && conversation.UserId == userId && conversation.Status == requestedStatus)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Select(conversation => ToDto(conversation))
            .ToListAsync(cancellationToken);
        return Ok(conversations);
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<IActionResult> GetConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        return conversation is null
            ? ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.")
            : Ok(ToDto(conversation));
    }

    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.");
        var messages = (await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId && (message.Role == ChatMessageRole.User || message.Role == ChatMessageRole.Assistant))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken))
            .Select(message => ToMessageDto(message, string.Equals(message.ProviderKey, "mock", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Ok(messages);
    }

    [HttpPatch("conversations/{conversationId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameConversation(Guid conversationId, RenameConversationRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.");
        conversation.Title = NormalizeTitle(request.Title) ?? conversation.Title;
        conversation.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(conversation));
    }

    [HttpPost("conversations/{conversationId:guid}/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.");
        conversation.Status = ConversationStatus.Archived;
        conversation.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(conversation));
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this, "Please enter a message.");
        var content = request.Content.Trim();
        if (content.Length == 0 || content.Length > ChatMessageLimits.MaximumContentLength)
            return ApiResults.Validation(this, "Please enter a message under 20,000 characters.");

        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.");
        if (conversation.Status == ConversationStatus.Archived)
            return ApiResults.Error(this, StatusCodes.Status409Conflict, "CONVERSATION_ARCHIVED", "Archived conversations cannot receive new messages.");

        var now = DateTime.UtcNow;
        if (conversation.Title == "New chat") conversation.Title = BuildTitle(content);
        conversation.UpdatedAt = now;
        conversation.LastMessageAt = now;
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = ChatMessageRole.User,
            Content = content,
            Status = ChatMessageStatus.Completed,
            CreatedAt = now,
        };
        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = ChatMessageRole.Assistant,
            Content = string.Empty,
            Status = ChatMessageStatus.Pending,
            CreatedAt = now.AddTicks(1),
        };
        db.ChatMessages.AddRange(userMessage, assistantMessage);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var history = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id && (message.Role == ChatMessageRole.User || message.Role == ChatMessageRole.Assistant) && message.Status == ChatMessageStatus.Completed)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(message => new AiChatMessage(message.Role.ToString().ToLowerInvariant(), message.Content))
                .ToListAsync(cancellationToken);
            var result = await completion.CompleteAsync(new AiChatRequest(history), cancellationToken);
            assistantMessage.Content = result.Content;
            assistantMessage.Status = ChatMessageStatus.Completed;
            assistantMessage.ProviderKey = result.Usage.ProviderKey;
            assistantMessage.ModelKey = result.Usage.ModelKey;
            assistantMessage.InputTokens = result.Usage.InputTokens;
            assistantMessage.OutputTokens = result.Usage.OutputTokens;
            assistantMessage.EstimatedCost = result.Usage.EstimatedCost;
            assistantMessage.ActualCost = result.Usage.ActualCost;
            assistantMessage.LatencyMs = result.Usage.LatencyMs;
            assistantMessage.FinishReason = result.Usage.FinishReason;
            conversation.UpdatedAt = DateTime.UtcNow;
            conversation.LastMessageAt = assistantMessage.CreatedAt;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new SendMessageResponse(ToDto(conversation), ToMessageDto(userMessage), ToMessageDto(assistantMessage, result.Usage.IsTestResponse)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            assistantMessage.Status = ChatMessageStatus.Failed;
            assistantMessage.Content = string.Empty;
            conversation.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Chat generation failed. ConversationId={ConversationId}; UserId={UserId}; TraceId={TraceId}", conversation.Id, GetUserId(), HttpContext.TraceIdentifier);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "AI_GENERATION_FAILED", "Taslim could not generate a response right now. Your message was saved.");
        }
    }

    private async Task<Conversation?> FindAuthorizedConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var conversation = await db.Conversations.FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null || conversation.UserId != userId) return null;
        return await access.IsMemberAsync(userId, conversation.WorkspaceId, cancellationToken) ? conversation : null;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

    private static ConversationDto ToDto(Conversation conversation) => new(conversation.Id, conversation.WorkspaceId, conversation.ProjectId, conversation.Title, conversation.Status.ToString(), conversation.CreatedAt, conversation.UpdatedAt, conversation.LastMessageAt);

    private static ChatMessageDto ToMessageDto(ChatMessage message, bool isTestResponse = false) => new(message.Id, message.ConversationId, message.Role.ToString(), message.Content, message.Status.ToString(), message.CreatedAt, isTestResponse);

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var clean = string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= 160 ? clean : clean[..157].TrimEnd() + "...";
    }

    private static string BuildTitle(string content)
    {
        var clean = string.Join(' ', content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var boundary = clean.IndexOfAny(['.', '!', '?', '\n']);
        if (boundary is > 0 and <= 80) clean = clean[..boundary];
        var title = NormalizeTitle(clean) ?? "New chat";
        return title.Length <= 60 ? title : title[..57].TrimEnd() + "...";
    }
}
