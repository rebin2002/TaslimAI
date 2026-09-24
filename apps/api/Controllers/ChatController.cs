using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class ChatController(
    TaslimDbContext db,
    WorkspaceAccessService access,
    IChatCompletionService completion,
    AiContextBuilder contextBuilder,
    IUsageLedgerService usageLedger,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> mvcJsonOptions,
    IFileStorageService fileStorage,
    IOptions<FileSettings> fileOptions,
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
            .OrderBy(message => message.Sequence)
            .ThenBy(message => message.CreatedAt)
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

    [HttpDelete("conversations/{conversationId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found.");

        // Preserve file bytes and usage records. Chat-message relationships cascade and usage
        // references are set to null by the existing relational configuration.
        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("conversations/{conversationId:guid}/messages/{messageId:guid}/regenerate")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Chat)]
    public async Task RegenerateMessage(Guid conversationId, Guid messageId, RegenerateMessageRequest request, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        if (!ModelState.IsValid)
        {
            await WriteEventAsync("message.failed", new { code = "VALIDATION_ERROR", message = "The regeneration request is invalid." }, cancellationToken);
            return;
        }

        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null)
        {
            await WriteEventAsync("message.failed", new { code = "CONVERSATION_NOT_FOUND", message = "Conversation not found." }, cancellationToken);
            return;
        }
        if (conversation.Status == ConversationStatus.Archived)
        {
            await WriteEventAsync("message.failed", new { code = "CONVERSATION_ARCHIVED", message = "Archived conversations cannot receive new messages." }, cancellationToken);
            return;
        }

        var assistant = await db.ChatMessages.FirstOrDefaultAsync(message =>
            message.Id == messageId &&
            message.ConversationId == conversationId &&
            message.Role == ChatMessageRole.Assistant &&
            message.Status == ChatMessageStatus.Completed,
            cancellationToken);
        if (assistant is null)
        {
            await WriteEventAsync("message.failed", new { code = "MESSAGE_NOT_REGENERABLE", message = "Only a completed assistant response can be regenerated." }, cancellationToken);
            return;
        }

        var laterUserMessageExists = await db.ChatMessages.AnyAsync(message =>
            message.ConversationId == conversationId &&
            message.Role == ChatMessageRole.User &&
            message.Sequence > assistant.Sequence,
            cancellationToken);
        if (laterUserMessageExists)
        {
            await WriteEventAsync("message.failed", new { code = "MESSAGE_NOT_REGENERABLE", message = "Regenerate the latest response before continuing the conversation." }, cancellationToken);
            return;
        }

        var sourceUser = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId && message.Role == ChatMessageRole.User && message.Sequence < assistant.Sequence)
            .OrderByDescending(message => message.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceUser is null)
        {
            await WriteEventAsync("message.failed", new { code = "MESSAGE_NOT_REGENERABLE", message = "The source message for this response is unavailable." }, cancellationToken);
            return;
        }

        await StreamRegenerationAsync(conversation, sourceUser, assistant.Id, request.RequestId.Trim(), cancellationToken);
    }

    private async Task StreamRegenerationAsync(Conversation conversation, ChatMessage sourceUser, Guid supersededAssistantId, string requestId, CancellationToken cancellationToken)
    {
        ChatMessage? assistant = null;
        UsageTransaction? usageTransaction = null;
        var persisted = false;
        try
        {
            var existing = await db.ChatMessages.FirstOrDefaultAsync(message => message.ConversationId == conversation.Id && message.RequestId == requestId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Role != ChatMessageRole.Assistant)
                {
                    await WriteEventAsync("message.failed", new { code = "REQUEST_ID_REUSED", message = "That regeneration request cannot be reused." }, cancellationToken);
                    return;
                }
                if (existing.Status == ChatMessageStatus.Completed)
                {
                    var completed = new SendMessageResponse(ToDto(conversation), ToMessageDto(sourceUser), ToMessageDto(existing, string.Equals(existing.ProviderKey, "mock", StringComparison.OrdinalIgnoreCase)));
                    await WriteEventAsync("message.started", new { conversation = completed.Conversation, userMessage = completed.UserMessage, assistantMessage = completed.AssistantMessage }, cancellationToken);
                    await WriteEventAsync("message.delta", new { messageId = completed.AssistantMessage.Id, delta = completed.AssistantMessage.Content }, cancellationToken);
                    await WriteEventAsync("message.completed", completed, cancellationToken);
                    return;
                }
                if (existing.Status == ChatMessageStatus.Pending)
                {
                    await WriteEventAsync("message.failed", new { code = "MESSAGE_IN_PROGRESS", message = "That regeneration is already in progress." }, cancellationToken);
                    return;
                }

                assistant = existing;
                ResetAssistantForRetry(assistant);
            }
            else
            {
                assistant = new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversation.Id,
                    RequestId = requestId,
                    Role = ChatMessageRole.Assistant,
                    Content = string.Empty,
                    Status = ChatMessageStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    Sequence = ++conversation.NextMessageSequence,
                };
                db.ChatMessages.Add(assistant);
            }

            conversation.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            usageTransaction = await usageLedger.GetOrCreatePendingAsync(conversation.WorkspaceId, GetUserId(), conversation.ProjectId, conversation.Id, requestId, UsageFeature.Chat, cancellationToken);
            await WriteEventAsync("message.started", new { conversation = ToDto(conversation), userMessage = ToMessageDto(sourceUser), assistantMessage = ToMessageDto(assistant) }, cancellationToken);

            var context = await BuildContextAsync(conversation, sourceUser.Id, cancellationToken, supersededAssistantId);
            var content = new StringBuilder();
            AiUsageMetadata? usage = null;
            await foreach (var item in completion.StreamAsync(context, cancellationToken))
            {
                switch (item)
                {
                    case AiMessageDelta delta when !string.IsNullOrEmpty(delta.Delta):
                        content.Append(delta.Delta);
                        await WriteEventAsync("message.delta", new { messageId = assistant.Id, delta = delta.Delta }, cancellationToken);
                        break;
                    case AiMessageCompleted completed:
                        usage = completed.Usage;
                        break;
                }
            }

            if (usage is null) throw new AiGenerationException("AI response did not complete.");
            PersistRegenerationSuccess(conversation, assistant, new AiGenerationResult(content.ToString(), usage));
            await db.SaveChangesAsync(CancellationToken.None);
            await usageLedger.CompleteAsync(usageTransaction, usage, CancellationToken.None);
            persisted = true;
            await WriteEventAsync("message.completed", new SendMessageResponse(ToDto(conversation), ToMessageDto(sourceUser), ToMessageDto(assistant, usage.IsTestResponse)), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (!persisted && assistant is not null)
            {
                await PersistRegenerationFailureAsync(conversation, assistant);
                if (usageTransaction is not null) await usageLedger.CancelAsync(usageTransaction, UsageFailureCodes.FromException(new OperationCanceledException()), CancellationToken.None);
                logger.LogInformation("Chat regeneration cancelled. ConversationId={ConversationId}; TraceId={TraceId}", conversation.Id, HttpContext.TraceIdentifier);
            }
        }
        catch (Exception exception)
        {
            if (!persisted && assistant is not null)
            {
                await PersistRegenerationFailureAsync(conversation, assistant);
                if (usageTransaction is not null) await usageLedger.FailAsync(usageTransaction, UsageFailureCodes.FromException(exception), cancellationToken: CancellationToken.None);
                LogGenerationFailure(exception, conversation.Id);
            }
            try { await WriteEventAsync("message.failed", new { code = "AI_GENERATION_FAILED", message = "Taslim could not regenerate a response right now." }, CancellationToken.None); } catch { /* client disconnected */ }
        }
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Chat)]
    public async Task<IActionResult> SendMessage(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this, "Please enter a message.");
        var prepared = await PrepareMessageAsync(conversationId, request, cancellationToken);
        if (prepared.Error is not null) return prepared.Error;
        if (prepared.ExistingResult is not null) return Ok(prepared.ExistingResult);

        UsageTransaction? usage = null;
        try
        {
            usage = await BeginUsageAsync(prepared, cancellationToken);
            var context = await BuildContextAsync(prepared.Conversation!, prepared.UserMessage!.Id, cancellationToken);
            var result = await completion.CompleteAsync(context, cancellationToken);
            PersistSuccess(prepared, result);
            await usageLedger.CompleteAsync(usage, result.Usage, cancellationToken);
            return Ok(new SendMessageResponse(ToDto(prepared.Conversation!), ToMessageDto(prepared.UserMessage!), ToMessageDto(prepared.AssistantMessage!, result.Usage.IsTestResponse)));
        }
        catch (FileContentUnavailableException)
        {
            await PersistFailureAsync(prepared, new FileContentUnavailableException());
            if (usage is not null) await usageLedger.FailAsync(usage, "ATTACHMENT_CONTENT_UNAVAILABLE", cancellationToken: CancellationToken.None);
            return AttachmentContentFailure();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await PersistFailureAsync(prepared, exception);
            if (usage is not null) await usageLedger.FailAsync(usage, UsageFailureCodes.FromException(exception), cancellationToken: CancellationToken.None);
            LogGenerationFailure(exception, prepared.Conversation!.Id);
            return GenerationFailure();
        }
        catch (OperationCanceledException exception)
        {
            await PersistFailureAsync(prepared, exception);
            if (usage is not null) await usageLedger.CancelAsync(usage, UsageFailureCodes.FromException(exception), CancellationToken.None);
            throw;
        }
    }

    [HttpPost("conversations/{conversationId:guid}/messages/stream")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Chat)]
    public async Task StreamMessage(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        if (!ModelState.IsValid)
        {
            await WriteEventAsync("message.failed", new { code = "VALIDATION_ERROR", message = "Please enter a message." }, cancellationToken);
            return;
        }

        PreparedChat? prepared = null;
        UsageTransaction? usageTransaction = null;
        var persisted = false;
        try
        {
            prepared = await PrepareMessageAsync(conversationId, request, cancellationToken);
            if (prepared.Error is not null)
            {
                await WriteEventAsync("message.failed", ErrorData(prepared.Error), cancellationToken);
                return;
            }

            if (prepared.ExistingResult is not null)
            {
                await WriteEventAsync("message.started", new
                {
                    conversation = prepared.ExistingResult.Conversation,
                    userMessage = prepared.ExistingResult.UserMessage,
                    assistantMessage = prepared.ExistingResult.AssistantMessage,
                }, cancellationToken);
                if (prepared.ExistingResult.AssistantMessage.Status == ChatMessageStatus.Completed.ToString())
                {
                    await WriteEventAsync("message.delta", new { messageId = prepared.ExistingResult.AssistantMessage.Id, delta = prepared.ExistingResult.AssistantMessage.Content }, cancellationToken);
                    await WriteEventAsync("message.completed", prepared.ExistingResult, cancellationToken);
                }
                else
                {
                    await WriteEventAsync("message.failed", new { code = "AI_GENERATION_FAILED", message = "Taslim could not generate a response right now." }, cancellationToken);
                }
                return;
            }

            usageTransaction = await BeginUsageAsync(prepared, cancellationToken);
            await WriteEventAsync("message.started", new
            {
                conversation = ToDto(prepared.Conversation!),
                userMessage = ToMessageDto(prepared.UserMessage!),
                assistantMessage = ToMessageDto(prepared.AssistantMessage!),
            }, cancellationToken);

            var context = await BuildContextAsync(prepared.Conversation!, prepared.UserMessage!.Id, cancellationToken);
            var content = new StringBuilder();
            AiUsageMetadata? usage = null;
            await foreach (var item in completion.StreamAsync(context, cancellationToken))
            {
                switch (item)
                {
                    case AiMessageDelta delta when !string.IsNullOrEmpty(delta.Delta):
                        content.Append(delta.Delta);
                        await WriteEventAsync("message.delta", new { messageId = prepared.AssistantMessage!.Id, delta = delta.Delta }, cancellationToken);
                        break;
                    case AiMessageCompleted completed:
                        usage = completed.Usage;
                        break;
                }
            }

            if (usage is null) throw new AiGenerationException("AI response did not complete.");
            PersistSuccess(prepared, new AiGenerationResult(content.ToString(), usage));
            await usageLedger.CompleteAsync(usageTransaction, usage, CancellationToken.None);
            persisted = true;
            var finalResponse = new SendMessageResponse(ToDto(prepared.Conversation!), ToMessageDto(prepared.UserMessage!), ToMessageDto(prepared.AssistantMessage!, usage.IsTestResponse));
            await WriteEventAsync("message.completed", finalResponse, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (!persisted && prepared is not null && prepared.ExistingResult is null)
            {
                await PersistFailureAsync(prepared, new AiGenerationException("The generation request was cancelled."));
                if (usageTransaction is not null) await usageLedger.CancelAsync(usageTransaction, UsageFailureCodes.FromException(new OperationCanceledException()), CancellationToken.None);
                logger.LogInformation("Chat generation cancelled. ConversationId={ConversationId}; TraceId={TraceId}", prepared.Conversation!.Id, HttpContext.TraceIdentifier);
            }
        }
        catch (FileContentUnavailableException)
        {
            if (!persisted && prepared is not null && prepared.ExistingResult is null)
            {
                await PersistFailureAsync(prepared, new FileContentUnavailableException());
                if (usageTransaction is not null) await usageLedger.FailAsync(usageTransaction, "ATTACHMENT_CONTENT_UNAVAILABLE", cancellationToken: CancellationToken.None);
            }
            try { await WriteEventAsync("message.failed", new { code = "ATTACHMENT_CONTENT_UNAVAILABLE", message = AttachmentContentFailureMessage }, CancellationToken.None); } catch { /* client disconnected */ }
        }
        catch (Exception exception)
        {
            if (!persisted && prepared is not null && prepared.ExistingResult is null)
            {
                await PersistFailureAsync(prepared, exception);
                if (usageTransaction is not null) await usageLedger.FailAsync(usageTransaction, UsageFailureCodes.FromException(exception), cancellationToken: CancellationToken.None);
                LogGenerationFailure(exception, prepared.Conversation!.Id);
            }
            try { await WriteEventAsync("message.failed", new { code = "AI_GENERATION_FAILED", message = "Taslim could not generate a response right now. Your message was saved." }, CancellationToken.None); } catch { /* client disconnected */ }
        }
    }

    private async Task<PreparedChat> PrepareMessageAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var content = request.Content.Trim();
        if (content.Length == 0 || content.Length > ChatMessageLimits.MaximumContentLength)
            return PreparedChat.Failure(ApiResults.Validation(this, "Please enter a message under 20,000 characters."));
        if (!string.IsNullOrWhiteSpace(request.RequestId) && request.RequestId.Trim().Length < 8)
            return PreparedChat.Failure(ApiResults.Validation(this, "The message request identifier is invalid."));
        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId.Trim();

        var conversation = await FindAuthorizedConversation(conversationId, cancellationToken);
        if (conversation is null) return PreparedChat.Failure(ApiResults.Error(this, StatusCodes.Status404NotFound, "CONVERSATION_NOT_FOUND", "Conversation not found."));
        if (conversation.Status == ConversationStatus.Archived)
            return PreparedChat.Failure(ApiResults.Error(this, StatusCodes.Status409Conflict, "CONVERSATION_ARCHIVED", "Archived conversations cannot receive new messages."));

        var existingUser = await db.ChatMessages.FirstOrDefaultAsync(message => message.ConversationId == conversationId && message.RequestId == requestId, cancellationToken);
        if (existingUser is not null)
        {
            if (!string.Equals(existingUser.Content, content, StringComparison.Ordinal))
                return PreparedChat.Failure(ApiResults.Error(this, StatusCodes.Status409Conflict, "REQUEST_ID_REUSED", "That message request cannot be reused with different content."));
            var existingAssistant = await db.ChatMessages.FirstOrDefaultAsync(message => message.ConversationId == conversationId && message.Role == ChatMessageRole.Assistant && message.CreatedAt > existingUser.CreatedAt, cancellationToken);
            if (existingAssistant is null) return PreparedChat.Failure(ApiResults.Error(this, StatusCodes.Status409Conflict, "MESSAGE_IN_PROGRESS", "That message is already being generated."));
            if (existingAssistant.Status == ChatMessageStatus.Failed)
            {
                existingAssistant.Status = ChatMessageStatus.Pending;
                existingAssistant.Content = string.Empty;
                existingAssistant.ProviderKey = null;
                existingAssistant.ModelKey = null;
                await db.SaveChangesAsync(cancellationToken);
                return PreparedChat.New(conversation, existingUser, existingAssistant);
            }
            return PreparedChat.FromExisting(new SendMessageResponse(ToDto(conversation), ToMessageDto(existingUser), ToMessageDto(existingAssistant, string.Equals(existingAssistant.ProviderKey, "mock", StringComparison.OrdinalIgnoreCase))));
        }

        var now = DateTime.UtcNow;
        if (conversation.Title == "New chat") conversation.Title = BuildTitle(content);
        conversation.UpdatedAt = now;
        conversation.LastMessageAt = now;
        var userSequence = conversation.NextMessageSequence + 1;
        var assistantSequence = userSequence + 1;
        conversation.NextMessageSequence = assistantSequence;
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            RequestId = requestId,
            Role = ChatMessageRole.User,
            Content = content,
            Status = ChatMessageStatus.Completed,
            CreatedAt = now,
            Sequence = userSequence,
        };
        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = ChatMessageRole.Assistant,
            Content = string.Empty,
            Status = ChatMessageStatus.Pending,
            CreatedAt = now.AddTicks(1),
            Sequence = assistantSequence,
        };
        db.ChatMessages.AddRange(userMessage, assistantMessage);
        var attachmentError = await AttachFilesAsync(conversation, userMessage, request.AttachmentIds, cancellationToken);
        if (attachmentError is not null) return PreparedChat.Failure(attachmentError);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var duplicate = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(message => message.ConversationId == conversationId && message.RequestId == requestId, cancellationToken);
            if (duplicate is not null) return PreparedChat.Failure(ApiResults.Error(this, StatusCodes.Status409Conflict, "MESSAGE_IN_PROGRESS", "That message is already being generated."));
            throw;
        }
        return PreparedChat.New(conversation, userMessage, assistantMessage);
    }

    private async Task<AiChatRequest> BuildContextAsync(Conversation conversation, Guid currentMessageId, CancellationToken cancellationToken, Guid? excludedMessageId = null)
    {
        var history = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id && (message.Role == ChatMessageRole.User || message.Role == ChatMessageRole.Assistant) && message.Status == ChatMessageStatus.Completed && (excludedMessageId == null || message.Id != excludedMessageId.Value))
            .OrderBy(message => message.Sequence)
            .ThenBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message => new AiChatMessage(message.Role.ToString().ToLowerInvariant(), message.Content))
            .ToListAsync(cancellationToken);
        var project = conversation.ProjectId is null
            ? null
            : await db.Projects.AsNoTracking()
                .Where(item => item.Id == conversation.ProjectId && item.WorkspaceId == conversation.WorkspaceId)
                .Select(item => new { item.Instructions, item.ContextNotes })
                .FirstOrDefaultAsync(cancellationToken);
        var memories = await db.PersonalMemories.AsNoTracking()
            .Where(memory => memory.UserId == GetUserId() && memory.WorkspaceId == conversation.WorkspaceId && memory.IsActive)
            .OrderByDescending(memory => memory.UpdatedAt)
            .ThenByDescending(memory => memory.CreatedAt)
            .Take(50)
            .Select(memory => new AiMemoryContext(memory.Category, memory.Title, memory.Content))
            .ToListAsync(cancellationToken);
        var attachmentCount = await db.ChatMessageAttachments.CountAsync(attachment => attachment.ChatMessageId == currentMessageId, cancellationToken);
        var files = await db.ChatMessageAttachments.AsNoTracking()
            .Where(attachment => attachment.ChatMessageId == currentMessageId)
            .OrderBy(attachment => attachment.SortOrder)
            .Select(attachment => new
            {
                attachment.StoredFileId,
                attachment.StoredFile.OriginalFileName,
                attachment.StoredFile.ContentType,
                attachment.StoredFile.Extension,
                attachment.StoredFile.ExtractedText,
                attachment.StoredFile.Status,
                attachment.StoredFile.TextExtractionStatus,
                attachment.StoredFile.ExtractedTextLength,
                attachment.StoredFile.StorageKey,
            })
            .ToListAsync(cancellationToken);
        if (files.Count != attachmentCount) throw new FileContentUnavailableException();
        var fileContexts = new List<AiFileContext>();
        foreach (var file in files)
        {
            logger.LogInformation("Chat attachment inspected. ConversationId={ConversationId}; MessageId={MessageId}; FileId={FileId}; Status={Status}; TextExtractionStatus={TextExtractionStatus}; ExtractedCharacterCount={ExtractedCharacterCount}", conversation.Id, currentMessageId, file.StoredFileId, file.Status, file.TextExtractionStatus, file.ExtractedTextLength ?? 0);
            if (file.Status != StoredFileStatus.Ready || (!FileContentTypes.IsImage(file.Extension) && (file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText))))
                throw new FileContentUnavailableException();
            string? dataUrl = null;
            if (FileContentTypes.IsImage(file.Extension))
            {
                try
                {
                    await using var stream = await fileStorage.OpenReadAsync(file.StorageKey, cancellationToken) ?? throw new FileContentUnavailableException();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken);
                    dataUrl = $"data:{file.ContentType};base64,{Convert.ToBase64String(buffer.ToArray())}";
                }
                catch (FileStorageUnavailableException)
                {
                    throw new FileContentUnavailableException();
                }
                catch (FileStorageOperationException)
                {
                    throw new FileContentUnavailableException();
                }
            }
            fileContexts.Add(new AiFileContext(file.OriginalFileName, file.ContentType, file.ExtractedText, dataUrl));
        }
        var attachmentCharacters = fileContexts.Sum(file => file.ExtractedText?.Length ?? 0);
        logger.LogInformation("Chat attachment context prepared. ConversationId={ConversationId}; MessageId={MessageId}; AttachmentCount={AttachmentCount}; ContextIncluded={ContextIncluded}; AttachmentContextCharacterCount={AttachmentContextCharacterCount}; AttachmentContextTokenEstimate={AttachmentContextTokenEstimate}", conversation.Id, currentMessageId, files.Count, fileContexts.Count > 0, attachmentCharacters, (attachmentCharacters + 3) / 4);
        return contextBuilder.Build(history, project?.Instructions, project?.ContextNotes, memories, fileContexts);
    }

    private async Task<IActionResult?> AttachFilesAsync(Conversation conversation, ChatMessage message, IReadOnlyCollection<Guid> requestedIds, CancellationToken cancellationToken)
    {
        var ids = requestedIds.Distinct().ToList();
        var maximumAttachments = Math.Clamp(fileOptions.Value.MaxAttachmentsPerMessage, 1, 20);
        if (ids.Count > maximumAttachments) return ApiResults.Validation(this, $"You can attach up to {maximumAttachments} files to one message.");
        if (ids.Count == 0) return null;
        var userId = GetUserId();
        var files = await db.StoredFiles
            .Where(file => ids.Contains(file.Id)
                && file.WorkspaceId == conversation.WorkspaceId
                && file.UserId == userId
                && (file.ProjectId == null || file.ProjectId == conversation.ProjectId)
                && (file.ConversationId == null || file.ConversationId == conversation.Id))
            .ToListAsync(cancellationToken);
        if (files.Count != ids.Count) return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ATTACHMENT_NOT_AVAILABLE", "One or more selected files are unavailable.");
        if (files.Any(file => file.Status != StoredFileStatus.Ready || (!FileContentTypes.IsImage(file.Extension) && (file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))))
            return ApiResults.Error(this, StatusCodes.Status422UnprocessableEntity, "ATTACHMENT_CONTENT_UNAVAILABLE", AttachmentContentFailureMessage);
        for (var index = 0; index < ids.Count; index++)
        {
            db.ChatMessageAttachments.Add(new ChatMessageAttachment { ChatMessageId = message.Id, StoredFileId = ids[index], SortOrder = index });
        }
        return null;
    }

    private static void PersistSuccess(PreparedChat prepared, AiGenerationResult result)
    {
        var assistant = prepared.AssistantMessage!;
        assistant.Content = result.Content;
        assistant.Status = ChatMessageStatus.Completed;
        assistant.ProviderKey = result.Usage.ProviderKey;
        assistant.ModelKey = result.Usage.ModelKey;
        assistant.InputTokens = result.Usage.InputTokens;
        assistant.CachedInputTokens = result.Usage.CachedInputTokens;
        assistant.OutputTokens = result.Usage.OutputTokens;
        assistant.EstimatedCost = result.Usage.EstimatedCost;
        assistant.ActualCost = result.Usage.ActualCost;
        assistant.LatencyMs = result.Usage.LatencyMs;
        assistant.FinishReason = result.Usage.FinishReason;
        prepared.Conversation!.UpdatedAt = DateTime.UtcNow;
        prepared.Conversation.LastMessageAt = assistant.CreatedAt;
    }

    private static void PersistRegenerationSuccess(Conversation conversation, ChatMessage assistant, AiGenerationResult result)
    {
        assistant.Content = result.Content;
        assistant.Status = ChatMessageStatus.Completed;
        assistant.ProviderKey = result.Usage.ProviderKey;
        assistant.ModelKey = result.Usage.ModelKey;
        assistant.InputTokens = result.Usage.InputTokens;
        assistant.CachedInputTokens = result.Usage.CachedInputTokens;
        assistant.OutputTokens = result.Usage.OutputTokens;
        assistant.EstimatedCost = result.Usage.EstimatedCost;
        assistant.ActualCost = result.Usage.ActualCost;
        assistant.LatencyMs = result.Usage.LatencyMs;
        assistant.FinishReason = result.Usage.FinishReason;
        conversation.UpdatedAt = DateTime.UtcNow;
        conversation.LastMessageAt = assistant.CreatedAt;
    }

    private static void ResetAssistantForRetry(ChatMessage assistant)
    {
        assistant.Status = ChatMessageStatus.Pending;
        assistant.Content = string.Empty;
        assistant.ProviderKey = null;
        assistant.ModelKey = null;
        assistant.InputTokens = null;
        assistant.CachedInputTokens = null;
        assistant.OutputTokens = null;
        assistant.EstimatedCost = null;
        assistant.ActualCost = null;
        assistant.LatencyMs = null;
        assistant.FinishReason = null;
    }

    private Task<UsageTransaction> BeginUsageAsync(PreparedChat prepared, CancellationToken cancellationToken) => usageLedger.GetOrCreatePendingAsync(
        prepared.Conversation!.WorkspaceId,
        GetUserId(),
        prepared.Conversation.ProjectId,
        prepared.Conversation.Id,
        prepared.UserMessage!.RequestId!,
        UsageFeature.Chat,
        cancellationToken);

    private async Task PersistFailureAsync(PreparedChat prepared, Exception exception)
    {
        try
        {
            prepared.AssistantMessage!.Status = ChatMessageStatus.Failed;
            prepared.AssistantMessage.Content = string.Empty;
            prepared.Conversation!.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception persistException)
        {
            logger.LogError(persistException, "Failed to persist AI failure state. ConversationId={ConversationId}; TraceId={TraceId}", prepared.Conversation?.Id, HttpContext.TraceIdentifier);
        }
    }

    private async Task PersistRegenerationFailureAsync(Conversation conversation, ChatMessage assistant)
    {
        try
        {
            assistant.Status = ChatMessageStatus.Failed;
            assistant.Content = string.Empty;
            conversation.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception persistException)
        {
            logger.LogError(persistException, "Failed to persist chat regeneration failure. ConversationId={ConversationId}; TraceId={TraceId}", conversation.Id, HttpContext.TraceIdentifier);
        }
    }

    private void LogGenerationFailure(Exception exception, Guid conversationId) => logger.LogError(exception, "Chat generation failed. Provider architecture handled the failure. ConversationId={ConversationId}; UserId={UserId}; TraceId={TraceId}", conversationId, GetUserId(), HttpContext.TraceIdentifier);

    private const string AttachmentContentFailureMessage = "We received your file, but couldn't read its contents. Please try the file again.";

    private IActionResult AttachmentContentFailure() => ApiResults.Error(this, StatusCodes.Status422UnprocessableEntity, "ATTACHMENT_CONTENT_UNAVAILABLE", AttachmentContentFailureMessage);

    private static object ErrorData(IActionResult result) => result is ObjectResult { Value: ErrorEnvelope envelope }
        ? new { code = envelope.Error.Code, message = envelope.Error.Message }
        : new { code = "CHAT_REQUEST_FAILED", message = "Taslim could not process that chat request." };

    private IActionResult GenerationFailure() => ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "AI_GENERATION_FAILED", "Taslim could not generate a response right now. Your message was saved.");

    private async Task<Conversation?> FindAuthorizedConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var conversation = await db.Conversations.FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null || conversation.UserId != userId) return null;
        return await access.IsMemberAsync(userId, conversation.WorkspaceId, cancellationToken) ? conversation : null;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

    private async Task WriteEventAsync(string type, object data, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {type}\n", cancellationToken);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(data, mvcJsonOptions.Value.JsonSerializerOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static ConversationDto ToDto(Conversation conversation) => new(conversation.Id, conversation.WorkspaceId, conversation.ProjectId, conversation.Title, conversation.Status.ToString(), conversation.CreatedAt, conversation.UpdatedAt, conversation.LastMessageAt);

    private static ChatMessageDto ToMessageDto(ChatMessage message, bool isTestResponse = false) => new(message.Id, message.ConversationId, message.Role.ToString(), message.Content, message.Status.ToString(), message.CreatedAt, message.Sequence, isTestResponse);

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

    private sealed class PreparedChat
    {
        public Conversation? Conversation { get; private init; }
        public ChatMessage? UserMessage { get; private init; }
        public ChatMessage? AssistantMessage { get; private init; }
        public IActionResult? Error { get; private init; }
        public SendMessageResponse? ExistingResult { get; private init; }
        public static PreparedChat New(Conversation conversation, ChatMessage user, ChatMessage assistant) => new() { Conversation = conversation, UserMessage = user, AssistantMessage = assistant };
        public static PreparedChat FromExisting(SendMessageResponse existing) => new() { ExistingResult = existing };
        public static PreparedChat Failure(IActionResult error) => new() { Error = error };
    }
}
