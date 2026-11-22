using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Regression coverage for the conversation search filter and paging around
/// archived/revoked workspaces, plus the bounded-query (100 UTF-16 unit) cap.
/// All tests are provider-free: they exercise the HTTP surface against the
/// SQLite-backed test factory and never activate an AI provider.
/// </summary>
public sealed class ConversationSearchQueryCapTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!123";
    private readonly TaslimApiFactory factory;

    public ConversationSearchQueryCapTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Search_finds_conversation_whose_exact_title_is_cut_mid_surrogate_pair()
    {
        // The title is 101 UTF-16 units and ends with an astral emoji. The search
        // surface bounds queries to 100 units, so the user's own exact title must
        // still resolve to their conversation. Truncating at the raw unit boundary
        // severs the surrogate pair; the corrupted needle then matches nothing.
        var prefix = new string('a', 99);
        var title = prefix + "\U0001F50D"; // U+1F50D => high surrogate D83D + low DD0D

        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"Query Cap Searcher {Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                Title = title,
                Status = ConversationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var body = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(title)}");
        Assert.NotNull(body);
        Assert.Contains(
            body!.Groups.SelectMany(group => group.Items),
            item => item.Type == GlobalSearchResultTypes.Conversation && item.Id == conversationId);
    }

    [Fact]
    public async Task Search_returns_a_valid_query_without_a_split_surrogate_pair()
    {
        var prefix = new string('b', 99);
        var longQuery = prefix + "\U0001F600"; // 101 UTF-16 units

        using var client = factory.CreateClient();
        await RegisterAsync(client, $"Query Cap Echo {Guid.NewGuid():N}");

        var body = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(longQuery)}");
        Assert.NotNull(body);
        Assert.True(body!.Query.Length <= 100, $"Echoed query was {body.Query.Length} UTF-16 units.");
        // A query cut mid surrogate pair emits an orphaned half-character; whatever
        // transport encoding a database or the JSON pipeline applies, the bounded
        // query the client sees must never be the replacement glyph the split
        // produces, and must equal the intact prefix.
        Assert.DoesNotContain("\uFFFD", body.Query);
        Assert.Equal(prefix, body.Query);
    }

    [Fact]
    public async Task Search_still_bounds_a_plain_query_to_the_documented_length()
    {
        var prefix = new string('c', 150);

        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"Query Cap Plain {Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                Title = new string('c', 120),
                Status = ConversationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var body = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(prefix)}");
        Assert.NotNull(body);
        Assert.Equal(100, body!.Query.Length);
        Assert.Contains(
            body.Groups.SelectMany(group => group.Items),
            item => item.Type == GlobalSearchResultTypes.Conversation && item.Id == conversationId);
    }

    [Fact]
    public async Task Search_surfaces_archived_conversation_in_an_archived_workspace_with_its_lifecycle_status()
    {
        // Pinned behavior from the archived/revoked workspace inspection:
        // search authorization is membership-derived, exactly like the chat
        // list/read endpoints. An archived workspace the user still belongs to
        // remains searchable, and an archived conversation is surfaced with its
        // lifecycle status so clients can distinguish it.
        var marker = $"Archived ws probe {Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"Archived Workspace Searcher {Guid.NewGuid():N}");
        var workspaceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Archived Business Workspace",
                Slug = $"archived-ws-{Guid.NewGuid():N}",
                Type = WorkspaceType.Business,
                IsArchived = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = auth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = now,
            });
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = workspaceId,
                UserId = auth.User.Id,
                Title = marker,
                Status = ConversationStatus.Archived,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var body = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(marker)}");
        Assert.NotNull(body);
        var hit = Assert.Single(
            body!.Groups.SelectMany(group => group.Items),
            item => item.Type == GlobalSearchResultTypes.Conversation && item.Id == conversationId);
        Assert.Equal(ConversationStatus.Archived.ToString(), hit.Status);
    }

    [Fact]
    public async Task Search_excludes_conversations_after_workspace_membership_is_revoked()
    {
        // Pinned behavior from the revoked-workspace inspection: revoking the
        // membership removes the workspace's conversations from every search
        // page, including its count contribution, on the next request.
        var marker = $"Revoked ws probe {Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"Revoked Workspace Searcher {Guid.NewGuid():N}");
        var workspaceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Revocable Business Workspace",
                Slug = $"revocable-ws-{Guid.NewGuid():N}",
                Type = WorkspaceType.Business,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = auth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = now,
            });
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = workspaceId,
                UserId = auth.User.Id,
                Title = marker,
                Status = ConversationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var before = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(marker)}");
        Assert.NotNull(before);
        Assert.Contains(
            before!.Groups.SelectMany(group => group.Items),
            item => item.Type == GlobalSearchResultTypes.Conversation && item.Id == conversationId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.RemoveRange(
                await db.WorkspaceMembers.Where(member => member.WorkspaceId == workspaceId && member.UserId == auth.User.Id).ToListAsync());
            await db.SaveChangesAsync();
        }

        var after = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(marker)}");
        Assert.NotNull(after);
        Assert.DoesNotContain(
            after!.Groups.SelectMany(group => group.Items),
            item => item.Id == conversationId);
        Assert.Equal(0, after.TotalCount);
    }

    [Fact]
    public async Task Search_pages_conversation_group_with_stable_counts_across_archived_and_active_results()
    {
        // Paging inspection across mixed archived/active conversations: each page
        // reports the same total, per-group hasMore transitions once at the exact
        // boundary, and the archived entry keeps its lifecycle status wherever it
        // lands in the ordering.
        var marker = $"Mixed paging {Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"Paging Searcher {Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            for (var index = 0; index < 3; index++)
            {
                db.Conversations.Add(new Conversation
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = auth.PersonalWorkspace.Id,
                    UserId = auth.User.Id,
                    Title = $"{marker} active {index}",
                    Status = ConversationStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now.AddMinutes(index),
                });
            }

            db.Conversations.Add(new Conversation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                Title = $"{marker} archived",
                Status = ConversationStatus.Archived,
                CreatedAt = now,
                UpdatedAt = now.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        var seenIds = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            var body = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(marker)}&page={page}&limit=1");
            Assert.NotNull(body);
            Assert.Equal(4, body!.TotalCount);
            var group = body.Groups.SingleOrDefault(item => item.Type == GlobalSearchResultTypes.Conversation);
            if (page == 5)
            {
                Assert.Null(group);
                Assert.False(body.HasMore);
                break;
            }

            Assert.NotNull(group);
            Assert.Equal(4, group!.Count);
            Assert.Equal(page < 4, group.HasMore);
            var item = Assert.Single(group.Items);
            Assert.DoesNotContain(item.Id, seenIds);
            seenIds.Add(item.Id);
        }

        Assert.Equal(4, seenIds.Count);

        var archived = await client.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={Uri.EscapeDataString(marker)}&page=1&limit=1");
        Assert.NotNull(archived);
        var firstPageItem = Assert.Single(archived!.Groups.Single(group => group.Type == GlobalSearchResultTypes.Conversation).Items);
        Assert.Equal(ConversationStatus.Archived.ToString(), firstPageItem.Status);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"{Guid.NewGuid():N}@example.com",
            password = Password,
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
