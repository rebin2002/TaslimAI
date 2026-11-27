using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only reconciliation of issue #350: synthetic persisted assetId -> authorized
/// GenerationJob ResultJson -> Social Studio "succeeded"/preview-ready.
///
/// Nothing in this file changes production behaviour and nothing here calls a provider,
/// a billing path or a migration: every run uses <see cref="SocialGenerationApiFactory"/>,
/// which swaps <c>ISocialGenerationProvider</c> for the deterministic in-process fixture.
///
/// The three existing lanes each prove one link of the chain and never meet:
///   - PR #878 <c>SocialGenerationResultPayloadTests</c> asserts the persisted result
///     against a hard-coded camelCase array inside the C# suite;
///   - PR #380/#425 <c>SocialPersistedAssetResultTests</c> asserts the asset-ID identity
///     only, on an integration branch that is now far behind <c>main</c>;
///   - PR #1046 <c>socialStudioResultContract.test.ts</c> asserts the state machine against
///     a hand-written TypeScript fixture.
/// Each side therefore encodes its own copy of the contract. This file adds the three links
/// that none of those lanes can express on their own:
///   1. a drift fence between the shipped producer contract type and the shipped browser
///      consumer, read straight out of <c>apps/web/src/lib/socialStudioState.ts</c> (the
///      same source-of-truth technique as <see cref="MusicLyricsDraftContractMirrorTests"/>),
///      so a rename on either side fails here instead of silently degrading the studio;
///   2. the end-to-end verdict for a REAL persisted payload under the mirrored shipped
///      predicate - the assertion that reproduces #350 on current <c>main</c> and turns
///      green exactly when #878's producer fix lands;
///   3. the provider-free cross-user negative control for the identifier #350 publishes:
///      the persisted <c>assetId</c> that reaches the browser must grant a second user
///      nothing - not the job, not its result, not the asset, not its bytes.
///
/// Expected state on current <c>main</c>: the fence (1) and the disclosure controls (3)
/// pass; <c>Successful_social_job_result_is_readable_by_the_shipped_studio_consumer</c>
/// fails with the actual persisted member names in its message. That failure IS the #350
/// acceptance gate; it is the reason this pull request stays a draft until #878 merges.
/// </summary>
public sealed class SocialResultContractReconciliationTests : IClassFixture<SocialGenerationApiFactory>
{
    private const string ConsumerSourcePath = "apps/web/src/lib/socialStudioState.ts";

    private readonly SocialGenerationApiFactory factory;

    public SocialResultContractReconciliationTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Producer_contract_members_match_the_names_the_shipped_studio_consumer_reads()
    {
        var accepted = ConsumerPostMembers();
        var required = ConsumerRequiredPostMembers();
        var produced = ProducerPostMembers();

        // The browser predicate only ever leaves "completed-unavailable" for a finished
        // job when a post lacks one of its required members, so those names are the
        // contract, not an implementation detail of the producer.
        Assert.True(accepted.Count > 0, $"No post members were read from {ConsumerSourcePath}.");
        Assert.True(required.Count > 0, $"No required post members were read from {ConsumerSourcePath}.");
        Assert.True(accepted.IsSupersetOf(required),
            $"The consumer requires {Join(required)} but only accepts {Join(accepted)}.");

        // Both #878's C# constant list and #1046's TypeScript fixture hard-code these
        // names. This pins them to the two shipped sources instead, so a rename of
        // SocialPost or of parsePosts fails here rather than drifting unnoticed.
        Assert.True(accepted.SetEquals(produced),
            $"Issue #350 naming drift: the producer contract projects to {Join(produced)} but the shipped studio consumer reads {Join(accepted)}.");

        // The failure mode recorded for #350 is CLR casing reaching the wire; the
        // consumer's accepted members must therefore all be camelCase.
        Assert.All(accepted, name => Assert.Equal(name, JsonNamingPolicy.CamelCase.ConvertName(name)));
    }

    [Fact]
    public void Shipped_studio_consumer_requires_the_persisted_asset_id_alongside_posts()
    {
        var rootMembers = ConsumerRootMembers();
        Assert.Contains("assetId", rootMembers);
        Assert.Contains("posts", rootMembers);

        // #350's acceptance criterion is that both arrive together. The consumer must
        // keep requiring the identifier so no lane can "fix" the studio by dropping it.
        Assert.DoesNotContain("AssetId", rootMembers);
        Assert.DoesNotContain("asset_id", rootMembers);
    }

    [Fact]
    public async Task Successful_social_job_result_is_readable_by_the_shipped_studio_consumer()
    {
        var run = await CompleteSocialGenerationAsync("Announce the interoperability lab to regional partners.");

        using var document = JsonDocument.Parse(run.Job.ResultJson!);
        var root = document.RootElement;

        // Real assetId contract: the identifier is the persisted Asset.Id (the same
        // identity #380 proves on its own stale base), re-anchored on current main.
        var publishedAssetId = ReadGuid(root, "assetId");
        Assert.NotNull(publishedAssetId);
        Assert.Equal(run.Asset.Id, publishedAssetId!.Value);
        Assert.Equal(run.OwnerUserId, run.Asset.CreatedByUserId);
        Assert.Equal(run.WorkspaceId, run.Asset.WorkspaceId);

        var posts = root.GetProperty("posts");
        Assert.Equal(JsonValueKind.Array, posts.ValueKind);
        var published = posts.EnumerateArray().ToArray();
        Assert.NotEmpty(published);

        var accepted = ConsumerPostMembers();
        var required = ConsumerRequiredPostMembers();
        foreach (var post in published)
        {
            Assert.Equal(JsonValueKind.Object, post.ValueKind);
            var emitted = post.EnumerateObject().Select(property => property.Name).ToArray();
            foreach (var requiredMember in required)
                Assert.True(emitted.Contains(requiredMember, StringComparer.Ordinal),
                    $"Issue #350: the persisted result publishes posts as [{Join(emitted)}], but the shipped studio consumer " +
                    $"(apps/web/src/lib/socialStudioState.ts, parsePosts) reads posts by [{Join(accepted)}] and drops every " +
                    $"post that fails its guard on {Join(required)}. A real success therefore renders as completed-unavailable. " +
                    $"Publish the canonical member names (PR #878) for this acceptance gate to pass.");
        }

        Assert.Equal("succeeded", MirrorSocialStudioState(run.Job.Status!, root));
    }

    [Fact]
    public async Task Published_social_asset_id_grants_a_second_user_nothing()
    {
        using var ownerClient = factory.CreateClient();
        var run = await CompleteSocialGenerationAsync("Share the cooperative's harvest report with members.", ownerClient);

        using var strangerClient = factory.CreateClient();
        var stranger = await RegisterAsync(strangerClient);
        var publishedAssetId = run.Asset.Id.ToString();

        // The identifier that #350 deliberately puts into the browser payload must not
        // become an access capability. Every call below is made by a second, unrelated
        // account that is not a member of the creator's workspace - the only cross-user
        // read path the studio ever exposes an assetId to.
        var jobResponse = await strangerClient.GetAsync($"/api/generation/jobs/{run.Job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, jobResponse.StatusCode);
        var jobBody = await jobResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(publishedAssetId, jobBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultJson", jobBody, StringComparison.OrdinalIgnoreCase);

        var assetResponse = await strangerClient.GetAsync($"/api/assets/{run.Asset.Id}");
        Assert.Equal(HttpStatusCode.NotFound, assetResponse.StatusCode);

        var downloadResponse = await strangerClient.GetAsync($"/api/assets/{run.Asset.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);
        // A 404 carries an error envelope, so the fence is proved by content rather than
        // by size: none of the persisted artifact may come back under the published id.
        var downloadBody = await downloadResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(run.PreviewMarker, downloadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(publishedAssetId, downloadBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultJson", downloadBody, StringComparison.OrdinalIgnoreCase);

        var listedForeignAssets = await strangerClient.GetAsync($"/api/assets?workspaceId={run.WorkspaceId}");
        Assert.Equal(HttpStatusCode.Forbidden, listedForeignAssets.StatusCode);

        var ownList = await strangerClient.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={stranger.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=50");
        Assert.NotNull(ownList);
        Assert.DoesNotContain(ownList.Items, item => item.Id == run.Job.Id);

        // Positive controls: the fence above is authorization, not an unavailable route.
        var ownerJob = await ownerClient.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{run.Job.Id}");
        Assert.NotNull(ownerJob);
        Assert.Equal("Succeeded", ownerJob.Status);
        Assert.Contains(publishedAssetId, ownerJob.ResultJson);

        var ownerAsset = await ownerClient.GetAsync($"/api/assets/{run.Asset.Id}");
        Assert.Equal(HttpStatusCode.OK, ownerAsset.StatusCode);
    }

    [Fact]
    public async Task Failed_social_job_publishes_no_asset_id_for_anyone_to_replay()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);

        // A reference to an asset that does not exist fails closed before publication,
        // so the success contract above can never be reached with a fabricated identifier.
        var response = await SendAsync(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Reference a missing visual to prove the run fails closed.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            includeHashtags = false,
            includeEmojis = false,
            generateVariants = false,
            assetIds = new[] { Guid.NewGuid() },
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(client, created.Job.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.True(string.IsNullOrEmpty(terminal.ResultJson),
            $"A failed Social job must not publish a result payload, but emitted: {terminal.ResultJson}");
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.Assets.CountAsync(item => item.SourceGenerationJobId == created.Job.Id));
    }

    private sealed record SocialRun(
        GenerationJobDto Job,
        Asset Asset,
        Guid OwnerUserId,
        Guid WorkspaceId,
        string PreviewMarker);

    private async Task<SocialRun> CompleteSocialGenerationAsync(string prompt, HttpClient? client = null)
    {
        var ownsClient = client is null;
        var ownerClient = client ?? factory.CreateClient();
        try
        {
            var auth = await RegisterAsync(ownerClient);
            var response = await SendAsync(ownerClient, new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                prompt,
                socialType = "announcement",
                platform = "linkedin",
                tone = "professional",
                language = "en",
                includeHashtags = true,
                includeEmojis = false,
                generateVariants = false,
                assetIds = Array.Empty<Guid>(),
                attachmentIds = Array.Empty<Guid>(),
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
            var terminal = await WaitForTerminalAsync(ownerClient, created.Job.Id);
            Assert.Equal("Succeeded", terminal.Status);
            Assert.False(string.IsNullOrWhiteSpace(terminal.ResultJson));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.AsNoTracking().SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
            Assert.Equal(AssetTypes.Social, asset.AssetType);

            // A casing-agnostic slice of the published preview text, used by the
            // disclosure controls: no cross-user response may ever carry it.
            using var result = JsonDocument.Parse(terminal.ResultJson!);
            var previewMarker = ReadAnyString(result.RootElement.GetProperty("posts")[0], "hook", "Hook")
                ?? throw new InvalidOperationException("The persisted Social result carried no post hook.");
            return new SocialRun(terminal, asset, auth.User.Id, auth.PersonalWorkspace.Id, previewMarker);
        }
        finally
        {
            if (ownsClient) ownerClient.Dispose();
        }
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var response = await SendAsync(client, new
        {
            displayName = "Social Reconciliation Tester",
            email = $"social-reconciliation-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        // Unique per call: the fixture factory is shared across these tests, so a reused
        // idempotency key would return an earlier job instead of exercising this run.
        request.Headers.Add("Idempotency-Key", $"social-reconciliation-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException("Social job did not reach a terminal state.");
    }

    private static string? ReadAnyString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        }

        return null;
    }

    private static Guid? ReadGuid(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return null;
        return Guid.TryParse(element.GetString(), out var value) ? value : null;
    }

    /// <summary>
    /// Mirrors socialStudioState() in apps/web/src/lib/socialStudioState.ts:
    /// <c>result?.assetId &amp;&amp; result.posts?.length ? "succeeded" : "completed-unavailable"</c>,
    /// where <c>posts</c> is the array that parsePosts keeps - only objects carrying a numeric
    /// <c>order</c> plus non-empty <c>hook</c> and <c>body</c> survive, under their exact names.
    /// </summary>
    private static string MirrorSocialStudioState(string status, JsonElement root)
    {
        switch (status)
        {
            case "Pending": return "pending";
            case "Queued": return "queued";
            case "Running": return "running";
            case "Failed": return "failed";
            case "Cancelled": return "cancelled";
        }

        var hasAssetId = root.TryGetProperty("assetId", out var assetId)
            && assetId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(assetId.GetString());

        var readablePosts = 0;
        if (root.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array)
        {
            foreach (var post in posts.EnumerateArray())
            {
                if (post.ValueKind != JsonValueKind.Object) continue;
                if (!IsNumber(post, "order") || !IsNonEmptyString(post, "hook") || !IsNonEmptyString(post, "body")) continue;
                readablePosts++;
            }
        }

        return hasAssetId && readablePosts > 0 ? "succeeded" : "completed-unavailable";
    }

    private static bool IsNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);

    private static bool IsNonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static IReadOnlySet<string> ProducerPostMembers() =>
        typeof(SocialPost).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> ConsumerPostMembers() =>
        MemberTokens(FunctionBody("function parsePosts"), "post").ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> ConsumerRequiredPostMembers()
    {
        // The guard clause is the requirement: parsePosts returns [] for any post that
        // fails it, so whatever it reads is exactly what the studio needs to leave
        // "completed-unavailable". Derived from the shipped file, never hard-coded here.
        var guard = FunctionBody("function parsePosts")
            .Split('\n')
            .First(line => line.Contains("isRecord(post)", StringComparison.Ordinal) && line.Contains("return []", StringComparison.Ordinal));
        return MemberTokens(guard, "post").ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ConsumerRootMembers() =>
        MemberTokens(FunctionBody("export function parseSocialJobResult"), "parsed").ToHashSet(StringComparer.Ordinal);

    private static string FunctionBody(string signature)
    {
        var source = File.ReadAllText(RepositoryAbsolute(ConsumerSourcePath));
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find `{signature}` in {ConsumerSourcePath}.");
        var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected `{signature}` to close with a column-zero brace in {ConsumerSourcePath}.");
        return source.Substring(start, end - start);
    }

    private static IEnumerable<string> MemberTokens(string text, string receiver) =>
        Regex.Matches(text, $@"\b{receiver}\.([A-Za-z][A-Za-z0-9]*)").Select(match => match.Groups[1].Value);

    private static string Join(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"\"{name}\""));

    private static string RepositoryAbsolute(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root from the test assembly."),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
