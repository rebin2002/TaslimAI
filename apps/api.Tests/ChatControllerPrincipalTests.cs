using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Principal-resolution coverage for the chat surface. The resolved identifier is not a display
/// value: it scopes workspace membership, per-user conversation isolation, attachment selection
/// and usage attribution. <c>Guid.Parse</c> used to accept
/// <c>00000000-0000-0000-0000-000000000000</c>, so a successfully authenticated but forged claim was
/// treated as a real caller identity, and any other unusable spelling threw
/// <see cref="FormatException"/>, which the global handler surfaces as a 500 instead of a 401. Both
/// behaviours are asserted closed here for every chat route, including the two streaming routes
/// whose event-stream handshake must not start before the principal is accepted. Collaborators are
/// unreachable stubs and the database has no schema, so a route that stopped guarding fails here by
/// touching one of them rather than by an ambiguous result. Fixtures are synthetic; no provider,
/// credential or network call is used.
/// </summary>
public sealed class ChatControllerPrincipalTests
{
    [Theory]
    // A missing claim, an empty claim, a value that is not a GUID at all, and the zero GUID that
    // Guid.Parse happily accepts: none of them may be treated as a caller.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_authenticated_principal_is_unauthorized_for_every_chat_action(string? identifier)
    {
        using var harness = new Harness();
        var controller = harness.Controller(identifier);
        var workspaceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var requests = new Func<Task<IActionResult>>[]
        {
            () => controller.CreateConversation(workspaceId, new CreateConversationRequest { Title = "Chat" }, CancellationToken.None),
            () => controller.ListConversations(workspaceId, null, CancellationToken.None),
            () => controller.GetConversation(conversationId, CancellationToken.None),
            () => controller.GetMessages(conversationId, CancellationToken.None),
            () => controller.RenameConversation(conversationId, new RenameConversationRequest { Title = "Renamed" }, CancellationToken.None),
            () => controller.ArchiveConversation(conversationId, CancellationToken.None),
            () => controller.DeleteConversation(conversationId, CancellationToken.None),
            () => controller.SendMessage(conversationId, new SendMessageRequest { Content = "Hello" }, CancellationToken.None),
        };

        foreach (var request in requests)
            Assert.IsType<UnauthorizedResult>(await request());

        Assert.Equal(0, harness.Usage.Calls);
        Assert.Equal(0, harness.Storage.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Streaming_chat_routes_answer_401_before_the_event_stream_handshake(string? identifier)
    {
        using var harness = new Harness();
        var controller = harness.Controller(identifier);
        var http = (DefaultHttpContext)controller.ControllerContext.HttpContext!;
        var conversationId = Guid.NewGuid();

        await controller.RegenerateMessage(conversationId, Guid.NewGuid(),
            new RegenerateMessageRequest { RequestId = "regen-000001" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.NotEqual("text/event-stream; charset=utf-8", http.Response.ContentType ?? string.Empty);
        Assert.Equal(0, harness.Completion.Calls);

        // The second stream is a fresh response so the first route's headers cannot mask it.
        controller = harness.Controller(identifier);
        http = (DefaultHttpContext)controller.ControllerContext.HttpContext!;
        await controller.StreamMessage(conversationId, new SendMessageRequest { Content = "Hello" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.NotEqual("text/event-stream; charset=utf-8", http.Response.ContentType ?? string.Empty);
        Assert.Equal(0, harness.Completion.Calls);
    }

    [Fact]
    public async Task Valid_principal_is_not_rejected_by_the_guard()
    {
        using var harness = new Harness();
        var controller = harness.Controller(Guid.NewGuid().ToString("D"));

        // A well-formed identifier clears the guard, so the request continues to the data layer,
        // which has no schema in this harness. Reaching it at all is the point: the guard must not
        // turn a legitimate caller into an anonymous rejection.
        await RunsPastGuard(() => controller.GetConversation(Guid.NewGuid(), CancellationToken.None));
        await RunsPastGuard(() => controller.ListConversations(Guid.NewGuid(), null, CancellationToken.None));
        await RunsPastGuard(() => controller.CreateConversation(Guid.NewGuid(),
            new CreateConversationRequest { Title = "Chat" }, CancellationToken.None));
    }

    private static async Task RunsPastGuard(Func<Task<IActionResult>> request)
    {
        try
        {
            var result = await request();
            Assert.IsNotType<UnauthorizedResult>(result);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any data-layer or collaborator failure proves the guard let the caller through.
        }

        await Task.CompletedTask;
    }

    /// <summary>Owns the unreachable collaborators and builds a controller for one claim spelling.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public Harness()
        {
            connection.Open();
            Db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            Access = new WorkspaceAccessService(Db);
            Usage = new UnreachableUsageLedger();
            Storage = new UnreachableStorage();
            Completion = new CountingChatCompletionService();
        }

        public TaslimDbContext Db { get; }
        public WorkspaceAccessService Access { get; }
        public UnreachableUsageLedger Usage { get; }
        public UnreachableStorage Storage { get; }
        public CountingChatCompletionService Completion { get; }
        // Batch-C (#891/#908) admission reads router readiness only, after the principal and
        // membership guards. A ready stub keeps this file's unreachable-by-design contract:
        // reaching Select here means a guard stopped guarding.
        public IAiModelRouter Router { get; } = new ReadyButUnreachableRouter();

        public ChatController Controller(string? identifier) => new(
            Db,
            Access,
            Completion,
            Router,
            new AiContextBuilder(Options.Create(new AiOptions())),
            Usage,
            Options.Create(new JsonOptions()),
            Storage,
            Options.Create(new Taslim.Api.Files.FileOptions()),
            NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        identifier is null
                            ? Array.Empty<Claim>()
                            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) },
                        authenticationType: "Test")),
                },
            },
        };

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }
    }

    /// <summary>Counts entry so a leaked stream is visible, but never produces a provider response.</summary>
    private sealed class CountingChatCompletionService : IChatCompletionService
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            throw new NotSupportedException("The chat provider must not be reached before the principal is accepted.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new NotSupportedException("The chat provider must not be reached before the principal is accepted.");
        }
    }

    private sealed class UnreachableUsageLedger : IUsageLedgerService
    {
        public int Calls { get; private set; }

        private Task Blocked()
        {
            Calls++;
            throw new NotSupportedException("Usage attribution must not be reached before the principal is accepted.");
        }

        private Task<T> Blocked<T>()
        {
            Calls++;
            throw new NotSupportedException("Usage attribution must not be reached before the principal is accepted.");
        }

        public Task<UsageTransaction> GetOrCreatePendingAsync(
            Guid workspaceId,
            Guid userId,
            Guid? projectId,
            Guid? conversationId,
            string requestId,
            UsageFeature feature,
            CancellationToken cancellationToken = default,
            Guid? generationJobId = null,
            decimal? estimatedProviderCostUsd = null,
            string? costEstimateJson = null) => Blocked<UsageTransaction>();

        public Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default) => Blocked();

        public Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default) => Blocked();

        public Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default) => Blocked();

        public Task<UsageAdjustmentResult?> RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) => Blocked<UsageAdjustmentResult?>();

        public Task<UsageAdjustmentResult?> ReverseAsync(UsageTransaction transaction, string idempotencyKey, string reason, CancellationToken cancellationToken = default) => Blocked<UsageAdjustmentResult?>();
    }

    private sealed class UnreachableStorage : IFileStorageService
    {
        public int Calls { get; private set; }
        public string ProviderKey => "unreachable";

        private Task Blocked()
        {
            Calls++;
            throw new NotSupportedException("File storage must not be reached before the principal is accepted.");
        }

        private Task<T> Blocked<T>()
        {
            Calls++;
            throw new NotSupportedException("File storage must not be reached before the principal is accepted.");
        }

        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Blocked();
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Blocked<Stream?>();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Blocked();
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Blocked<bool>();
    }

    /// <summary>
    /// Advertises chat generation as ready so the Batch-C admission gate never changes the
    /// outcome this file tests; provider selection stays unreachable-by-design.
    /// </summary>
    private sealed class ReadyButUnreachableRouter : IAiModelRouter
    {
        public AiProviderSelection Select(AiChatRequest request)
            => throw new NotSupportedException("Provider selection must not be reached before the principal is accepted.");

        public bool IsChatGenerationAvailable(bool hasImageAttachments) => true;
    }
}
