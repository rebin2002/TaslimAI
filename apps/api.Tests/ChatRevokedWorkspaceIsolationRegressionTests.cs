using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ChatRevokedWorkspaceIsolationRegressionTests : IClassFixture<ChatRevokedWorkspaceIsolationFactory>
{
    private const string Password = "StrongPassword!123";
    private readonly ChatRevokedWorkspaceIsolationFactory factory;

    public ChatRevokedWorkspaceIsolationRegressionTests(ChatRevokedWorkspaceIsolationFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Revoked_workspace_member_cannot_read_or_generate_chat_in_another_workspace()
    {
        using var actor = factory.CreateClient();
        var actorEmail = $"revoked-chat-member-{Guid.NewGuid():N}@example.test";
        var actorAuth = await Register(actor, "Revoked Chat Member", actorEmail);

        using var workspaceOwner = factory.CreateClient();
        var ownerAuth = await Register(workspaceOwner, "Shared Workspace Owner", $"revoked-chat-owner-{Guid.NewGuid():N}@example.test");

        var workspaceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var workspaceCanary = "Shared-workspace chat canary that must not leak after revocation.";
        var blockedPrompt = "This post-revocation prompt must never be saved or generated.";
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Revocable Shared Workspace",
                Slug = $"revocable-shared-{Guid.NewGuid():N}",
                Type = WorkspaceType.Business,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.WorkspaceMembers.AddRange(
                new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = ownerAuth.User.Id, Role = WorkspaceRole.Owner, JoinedAt = now },
                new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = actorAuth.User.Id, Role = WorkspaceRole.Member, JoinedAt = now });
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = workspaceId,
                UserId = actorAuth.User.Id,
                Title = "Shared workspace conversation",
                Status = ConversationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                LastMessageAt = now,
                NextMessageSequence = 3,
            });
            db.ChatMessages.AddRange(
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    Role = ChatMessageRole.User,
                    Content = "Seeded workspace conversation",
                    Status = ChatMessageStatus.Completed,
                    CreatedAt = now,
                    Sequence = 1,
                },
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    Role = ChatMessageRole.Assistant,
                    Content = workspaceCanary,
                    Status = ChatMessageStatus.Completed,
                    CreatedAt = now,
                    Sequence = 2,
                    ProviderKey = "provider-free-test",
                });
            await db.SaveChangesAsync();
        }

        var messagesPath = $"/api/conversations/{conversationId}/messages";
        var beforeRevocation = await actor.GetFromJsonAsync<List<ChatMessageDto>>(messagesPath);
        Assert.NotNull(beforeRevocation);
        Assert.Contains(beforeRevocation, message => message.Content == workspaceCanary);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var membership = await db.WorkspaceMembers.SingleAsync(member => member.WorkspaceId == workspaceId && member.UserId == actorAuth.User.Id);
            db.WorkspaceMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await actor.GetAsync("/api/auth/me")).StatusCode);
        var deniedRead = await actor.GetAsync(messagesPath);
        var deniedReadBody = await deniedRead.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, deniedRead.StatusCode);
        Assert.DoesNotContain(workspaceCanary, deniedReadBody, StringComparison.Ordinal);

        var deniedGeneration = await SendWithCsrf(actor, HttpMethod.Post, $"/api/conversations/{conversationId}/messages", new
        {
            content = blockedPrompt,
            requestId = Guid.NewGuid().ToString("N"),
        });
        var deniedGenerationBody = await deniedGeneration.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, deniedGeneration.StatusCode);
        Assert.DoesNotContain(workspaceCanary, deniedGenerationBody, StringComparison.Ordinal);
        Assert.Equal(0, factory.Completion.CallCount);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var persistedMessages = await db.ChatMessages.Where(message => message.ConversationId == conversationId).OrderBy(message => message.Sequence).ToListAsync();
            Assert.Equal(2, persistedMessages.Count);
            Assert.DoesNotContain(persistedMessages, message => message.Content == blockedPrompt);
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email,
            password = Password,
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class ChatRevokedWorkspaceIsolationFactory : TaslimApiFactory
{
    public CountingChatCompletionService Completion { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(Completion);
        });
    }
}

public sealed class CountingChatCompletionService : IChatCompletionService
{
    private static readonly AiUsageMetadata Usage = new("provider-free-test", "fake-model", 1, null, 1, 0m, 0m, 0, "stop", true);
    private int callCount;

    public int CallCount => Volatile.Read(ref callCount);

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        return Task.FromResult(new AiGenerationResult("fake completion", Usage));
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AiMessageDelta("fake stream");
        yield return new AiMessageCompleted(Usage);
    }
}
