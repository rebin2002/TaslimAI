using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.AcademicSources;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Issue #1267 slice 1 — the scholarly search endpoint as a browser sees it: real cookie
/// authentication, real workspace membership, real pipeline. The provider fan-out is
/// substituted because no test may reach the network. What is asserted here is precisely the
/// security surface the issue calls out: unauthenticated 401, cross-workspace 403, bounded
/// pages, and no discovery work before the fence has passed.
/// </summary>
public sealed class AcademicSourcesApiTests : IClassFixture<AcademicSourcesApiFactory>
{
    private static readonly string[] InternalCatalogKeys = ["openalex", "crossref", "openlibrary", "googlebooks", "googleapis"];
    private readonly AcademicSourcesApiFactory factory;

    public AcademicSourcesApiTests(AcademicSourcesApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Unauthenticated_request_is_rejected_before_any_discovery()
    {
        var anonymous = factory.CreateAnonymousClient();

        var response = await anonymous.GetAsync($"/api/workspaces/{Guid.NewGuid()}/academic-sources/search?q=market+access");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Discovery.CallCount);
    }

    [Fact]
    public async Task Cross_workspace_access_is_forbidden_and_makes_no_discovery_call()
    {
        factory.Discovery.Reset();
        var foreignWorkspace = factory.AddForeignWorkspace();

        var response = await factory.Client.GetAsync($"/api/workspaces/{foreignWorkspace}/academic-sources/search?q=market+access");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Discovery.CallCount);
    }

    [Fact]
    public async Task Member_search_returns_a_bounded_page_with_no_provider_identity()
    {
        factory.Discovery.Behavior = (query, _) => Task.FromResult(new AcademicDiscoveryBatch
        {
            Sources = Enumerable.Range(0, query.MaxResults).Select(index => new AcademicSourceRecord
            {
                ProviderKey = "openalex",
                ProviderWorkId = $"W{index}",
                Title = $"Synthesized study {index}",
                SourceType = AcademicSourceTypes.JournalArticle,
                AccessLevel = AcademicAccessLevel.Metadata,
                Doi = $"10.5555/synth.{index:D4}",
            }).ToArray(),
            ProviderOutcomes = [new AcademicProviderOutcome { ProviderKey = "openalex", Status = AcademicProviderOutcomeStatus.Ok, SourceCount = query.MaxResults }],
        });

        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?q=market+access&page=2&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(5, body.GetProperty("pageSize").GetInt32());
        Assert.Equal("Synthesized study 5", body.GetProperty("sources")[0].GetProperty("title").GetString());
        Assert.True(body.GetProperty("hasMore").GetBoolean());
        Assert.Equal(10, factory.Discovery.LastQuery!.MaxResults);
        foreach (var key in InternalCatalogKeys)
            Assert.False(json.Contains(key, StringComparison.OrdinalIgnoreCase), $"response leaked provider key '{key}'");
    }

    [Fact]
    public async Task Malformed_identifier_is_rejected_with_field_level_feedback()
    {
        factory.Discovery.Reset();

        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?isbn=9780306406111");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.True(body.GetProperty("error").GetProperty("fields").TryGetProperty("isbn", out _));
        Assert.Equal(0, factory.Discovery.CallCount);
    }

    [Fact]
    public async Task Requested_page_beyond_the_bound_is_rejected_instead_of_deepening_the_fan_out()
    {
        factory.Discovery.Reset();

        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?q=x&page=20&pageSize=25");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Discovery.CallCount);
    }

    [Fact]
    public async Task Discovery_is_handed_the_pipeline_cancellation_token()
    {
        // The endpoint must not pass CancellationToken.None: a client that goes away has to be
        // able to stop the provider work. The unit tests prove the controller forwards exactly
        // HttpContext.RequestAborted; here the observable contract is that the token that
        // reaches discovery came out of the real pipeline.
        CancellationToken seen = default;
        factory.Discovery.Behavior = (_, token) =>
        {
            seen = token;
            return Task.FromResult(new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = [] });
        };

        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?q=market+access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(seen.CanBeCanceled);
    }

    [Fact]
    public async Task Read_only_route_exposes_no_write_surface()
    {
        factory.Discovery.Reset();

        var get = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?q=market+access");
        var post = await factory.Client.PostAsync(
            $"/api/workspaces/{factory.WorkspaceId}/academic-sources/search",
            JsonContent.Create(new { q = "market access" }));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        // There is no write handler on this route, and the API's antiforgery boundary refuses an
        // untokenised state-changing call before MVC reaches the (nonexistent) action.
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var rejected = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_VALIDATION_FAILED", rejected.GetProperty("error").GetProperty("code").GetString());
    }
}

/// <summary>
/// The state every deployment that has not written the configuration section is in: DI is
/// wired and the route authenticates, but discovery is off, so the endpoint reports itself
/// unavailable and no socket is opened. Enabling egress stays a deployment decision, and this
/// test is what stops a wiring change from silently making that decision for production.
/// </summary>
public sealed class AcademicSourcesFailClosedApiTests : IClassFixture<AcademicSourcesUnconfiguredFactory>
{
    private readonly AcademicSourcesUnconfiguredFactory factory;

    public AcademicSourcesFailClosedApiTests(AcademicSourcesUnconfiguredFactory factory) => this.factory = factory;

    [Fact]
    public async Task Unconfigured_deployment_answers_service_unavailable_for_a_member()
    {
        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.WorkspaceId}/academic-sources/search?q=market+access");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACADEMIC_SEARCH_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unconfigured_deployment_still_fences_non_members_first()
    {
        var response = await factory.Client.GetAsync($"/api/workspaces/{factory.AddForeignWorkspace()}/academic-sources/search?q=market+access");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Real_discovery_graph_resolves_from_di_with_every_switch_off()
    {
        using var scope = factory.Services.CreateScope();
        Assert.IsType<AcademicSourceDiscoveryService>(scope.ServiceProvider.GetRequiredService<IAcademicSourceDiscoveryService>());
        // The composition root's adapter graph must exist even when every switch is off,
        // otherwise the integration line in Program.cs would be decoration.
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IAcademicWorkSearchProvider>());
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IAcademicBookCatalogProvider>());
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IAcademicDoiValidator>());
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AcademicSourceDiscoveryOptions>>().Value;
        Assert.False(options.Enabled);
    }
}

/// <summary>Shared SQLite-backed host with a registered student and their personal workspace.</summary>
public abstract class AcademicSourcesTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    protected AcademicSourcesTestFactory()
    {
        connection.Open();
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        Client = CreateClient();
        WorkspaceId = Register(Client, "Academic Search Student").PersonalWorkspace.Id;
    }

    public HttpClient Client { get; }
    public Guid WorkspaceId { get; }

    protected abstract bool DiscoveryEnabledAtComposition { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            if (DiscoveryEnabledAtComposition)
            {
                // Wired and enabled, with no provider switched on: the endpoint must be
                // reachable, and nothing may dial out.
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AcademicSourceDiscovery:Enabled"] = "true" });
            }
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            ConfigureFeatureServices(services);
        });
    }

    protected virtual void ConfigureFeatureServices(IServiceCollection services)
    {
    }

    public HttpClient CreateAnonymousClient() => CreateClient();

    public Guid AddForeignWorkspace()
    {
        var workspaceId = Guid.NewGuid();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Workspaces.Add(new Taslim.Api.Domain.Workspace
        {
            Id = workspaceId,
            Name = "Another student's workspace",
            Slug = $"other-{workspaceId:N}",
            Type = Taslim.Api.Domain.WorkspaceType.Business,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return workspaceId;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }

    private static AuthResponse Register(HttpClient client, string displayName)
    {
        var csrf = client.GetFromJsonAsync<JsonElement>("/api/auth/csrf").GetAwaiter().GetResult();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"academic-sources-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Seeded registration failed: {(int)response.StatusCode} {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        return response.Content.ReadFromJsonAsync<AuthResponse>().GetAwaiter().GetResult();
    }
}

/// <summary>Discovery switched on at composition, provider fan-out substituted by a script.</summary>
public sealed class AcademicSourcesApiFactory : AcademicSourcesTestFactory
{
    public ScriptedDiscovery Discovery { get; } = new();

    protected override bool DiscoveryEnabledAtComposition => true;

    protected override void ConfigureFeatureServices(IServiceCollection services)
    {
        services.RemoveAll<IAcademicSourceDiscoveryService>();
        services.AddSingleton<IAcademicSourceDiscoveryService>(Discovery);
    }
}

/// <summary>Host with no AcademicSourceDiscovery section at all — the shipped default.</summary>
public sealed class AcademicSourcesUnconfiguredFactory : AcademicSourcesTestFactory
{
    protected override bool DiscoveryEnabledAtComposition => false;
}

/// <summary>Scriptable stand-in for the provider fan-out. Records the token, never calls out.</summary>
public sealed class ScriptedDiscovery : IAcademicSourceDiscoveryService
{
    private Func<AcademicDiscoveryQuery, CancellationToken, Task<AcademicDiscoveryBatch>> defaultBehavior =
        (_, _) => Task.FromResult(new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = [] });

    public int CallCount { get; private set; }
    public AcademicDiscoveryQuery? LastQuery { get; private set; }

    public Func<AcademicDiscoveryQuery, CancellationToken, Task<AcademicDiscoveryBatch>> Behavior
    {
        get => behavior;
        set => behavior = value;
    }

    private Func<AcademicDiscoveryQuery, CancellationToken, Task<AcademicDiscoveryBatch>> behavior;

    public ScriptedDiscovery() => behavior = defaultBehavior;

    public void Reset()
    {
        CallCount = 0;
        LastQuery = null;
        behavior = defaultBehavior;
    }

    public Task<AcademicDiscoveryBatch> DiscoverAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastQuery = query;
        return behavior(query, cancellationToken);
    }
}
