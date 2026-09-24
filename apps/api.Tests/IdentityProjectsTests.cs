using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Taslim.Api.Tests;

public class TaslimApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public TaslimApiFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public sealed class IdentityProjectsTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public IdentityProjectsTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Registration_creates_personal_workspace_and_owner()
    {
        using var client = factory.CreateClient();
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        var response = await Register(client, "Workspace Owner", email);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.Equal(email, body.User.Email);
        Assert.Equal("Personal", body.PersonalWorkspace.Type);
        Assert.Equal("Owner", body.PersonalWorkspace.Role);
        Assert.Equal(body.PersonalWorkspace.Id, body.User.PersonalWorkspaceId);
    }

    [Fact]
    public async Task Duplicate_email_and_wrong_password_fail_generically()
    {
        using var client = factory.CreateClient();
        var email = $"duplicate-{Guid.NewGuid():N}@example.com";
        await Register(client, "Duplicate Owner", email);
        await Logout(client);
        var duplicate = await Register(client, "Duplicate Owner", email);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var wrong = await Login(client, email, "WrongPassword!123");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var error = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid email or password.", error.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Me_requires_authentication_and_logout_ends_session()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        var email = $"session-{Guid.NewGuid():N}@example.com";
        await Register(anonymous, "Session Owner", email);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        await Logout(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        var login = await Login(anonymous, email, "StrongPassword!123");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await Logout(anonymous);
    }

    [Fact]
    public async Task Project_lifecycle_is_owned_by_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Project Owner", $"project-{Guid.NewGuid():N}@example.com");
        var auth = await ownerAuth.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        var created = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", new { name = "Green Marketing", type = "Marketing", description = "A focused launch." });
        Assert.Equal("Active", created.Status);
        var list = await owner.GetFromJsonAsync<List<ProjectDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/projects?status=Active");
        Assert.Contains(list!, project => project.Id == created.Id);
        var updated = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Patch, $"/api/projects/{created.Id}", new { name = "Green Marketing Updated", type = "Marketing", description = "Updated." });
        Assert.Equal("Green Marketing Updated", updated.Name);
        await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/projects/{created.Id}/archive", null);
        var archived = await owner.GetFromJsonAsync<List<ProjectDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/projects?status=Archived");
        Assert.Contains(archived!, project => project.Id == created.Id);
        await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/projects/{created.Id}/restore", null);
        var activeAgain = await owner.GetFromJsonAsync<List<ProjectDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/projects?status=Active");
        Assert.Contains(activeAgain!, project => project.Id == created.Id);
    }

    [Fact]
    public async Task Project_overview_returns_project_resources_activity_and_user_conversations()
    {
        using var owner = factory.CreateClient();
        var authResponse = await Register(owner, "Overview Owner", $"overview-{Guid.NewGuid():N}@example.com");
        var auth = await authResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", new { name = "Overview Project", type = "Business" });
        var conversation = await SendWithCsrf<ConversationDto>(owner, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations", new { projectId = project.Id, title = "Project planning" });
        var job = await SendWithCsrf<GenerationJobDto>(owner, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, projectId = project.Id, jobType = "system.test", inputJson = "{}", title = "Project output" });

        var overview = await owner.GetFromJsonAsync<ProjectOverviewDto>($"/api/projects/{project.Id}/overview");
        Assert.NotNull(overview);
        Assert.Equal(project.Id, overview!.Project.Id);
        Assert.Equal(auth.PersonalWorkspace.Id, overview.Workspace.Id);
        Assert.Equal("Owner", overview.Workspace.Role);
        Assert.Equal(1, overview.Counts.Conversations);
        Assert.Equal(1, overview.Counts.Activity);
        Assert.Contains(overview.Conversations, item => item.Id == conversation.Id);
        Assert.Contains(overview.RecentActivity, item => item.JobId == job.Id && item.ProjectId == project.Id);

        var workspaces = await owner.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        Assert.Contains(workspaces!, item => item.Id == auth.PersonalWorkspace.Id && item.Role == "Owner" && item.Type == "Personal");
    }

    [Fact]
    public async Task Project_overview_does_not_cross_workspace_boundaries()
    {
        using var first = factory.CreateClient();
        var firstAuthResponse = await Register(first, "Overview First", $"overview-first-{Guid.NewGuid():N}@example.com");
        var firstAuth = await firstAuthResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(firstAuth);
        var project = await SendWithCsrf<ProjectDto>(first, HttpMethod.Post, $"/api/workspaces/{firstAuth.PersonalWorkspace.Id}/projects", new { name = "Private Overview" });

        using var second = factory.CreateClient();
        var secondAuthResponse = await Register(second, "Overview Second", $"overview-second-{Guid.NewGuid():N}@example.com");
        var secondAuth = await secondAuthResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(secondAuth);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync($"/api/projects/{project.Id}/overview")).StatusCode);
        var workspaces = await second.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        Assert.DoesNotContain(workspaces!, item => item.Id == firstAuth.PersonalWorkspace.Id);
        Assert.Contains(workspaces!, item => item.Id == secondAuth!.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Another_user_cannot_read_or_create_in_a_private_workspace()
    {
        using var first = factory.CreateClient();
        var firstAuthResponse = await Register(first, "First Owner", $"first-{Guid.NewGuid():N}@example.com");
        var firstAuth = await firstAuthResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(firstAuth);
        var project = await SendWithCsrf<ProjectDto>(first, HttpMethod.Post, $"/api/workspaces/{firstAuth.PersonalWorkspace.Id}/projects", new { name = "Private Project" });
        using var second = factory.CreateClient();
        await Register(second, "Second Owner", $"second-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync($"/api/projects/{project.Id}")).StatusCode);
        var create = await SendWithCsrf(second, HttpMethod.Post, $"/api/workspaces/{firstAuth.PersonalWorkspace.Id}/projects", new { name = "Intrusion" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Invalid_project_input_returns_controlled_error()
    {
        using var client = factory.CreateClient();
        var authResponse = await Register(client, "Validation Owner", $"validation-{Guid.NewGuid():N}@example.com");
        var auth = await authResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", new { name = "", type = "Unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Password_policy_endpoint_matches_configured_identity_requirements()
    {
        using var client = factory.CreateClient();
        var policy = await client.GetFromJsonAsync<JsonElement>("/api/auth/password-policy");
        Assert.Equal(10, policy.GetProperty("requiredLength").GetInt32());
        Assert.True(policy.GetProperty("requireUppercase").GetBoolean());
        Assert.True(policy.GetProperty("requireLowercase").GetBoolean());
        Assert.True(policy.GetProperty("requireDigit").GetBoolean());
        Assert.True(policy.GetProperty("requireNonAlphanumeric").GetBoolean());
    }

    [Fact]
    public async Task Csrf_endpoint_returns_a_usable_non_cacheable_token()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        request.Headers.Add("Origin", "http://localhost:3000");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http://localhost:3000", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Login_without_or_with_invalid_csrf_returns_safe_csrf_error_before_identity()
    {
        using var client = factory.CreateClient();
        var email = $"csrf-login-{Guid.NewGuid():N}@example.com";
        await Register(client, "CSRF Login User", email);
        await Logout(client);

        using var missing = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password = "StrongPassword!123" }),
        };
        var missingResponse = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.Equal("CSRF_VALIDATION_FAILED", (await missingResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password = "StrongPassword!123" }),
        };
        invalid.Headers.Add("X-CSRF-TOKEN", "invalid-token");
        var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal("CSRF_VALIDATION_FAILED", (await invalidResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_anonymous_login_csrf_is_rejected_then_fresh_token_allows_login()
    {
        using var client = factory.CreateClient();
        using var separateAnonymousClient = factory.CreateClient();
        var email = $"stale-login-{Guid.NewGuid():N}@example.com";
        var foreignToken = await GetCsrf(separateAnonymousClient);
        var anonymousToken = await GetCsrf(client);
        var registration = await SendWithToken(client, HttpMethod.Post, "/api/auth/register", anonymousToken, new
        {
            displayName = "Stale Login User",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        await Logout(client);

        var staleLogin = await SendWithToken(client, HttpMethod.Post, "/api/auth/login", foreignToken, new { email, password = "StrongPassword!123" });
        Assert.Equal(HttpStatusCode.BadRequest, staleLogin.StatusCode);
        Assert.Equal("CSRF_VALIDATION_FAILED", (await staleLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var freshLogin = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password = "StrongPassword!123" });
        Assert.Equal(HttpStatusCode.OK, freshLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Weak_registration_password_returns_safe_field_level_errors()
    {
        using var client = factory.CreateClient();
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Weak Password User",
            email = $"weak-{Guid.NewGuid():N}@example.com",
            password = "abcde",
            preferredLanguage = "en"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("error").GetProperty("code").GetString());
        var passwordErrors = error.GetProperty("error").GetProperty("fields").GetProperty("password").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("PASSWORD_TOO_SHORT", passwordErrors);
        Assert.Contains("PASSWORD_REQUIRES_UPPERCASE", passwordErrors);
        Assert.Contains("PASSWORD_REQUIRES_DIGIT", passwordErrors);
        Assert.Contains("PASSWORD_REQUIRES_NON_ALPHANUMERIC", passwordErrors);
        Assert.DoesNotContain("PASSWORD_REQUIRES_LOWERCASE", passwordErrors);
        Assert.DoesNotContain("StackTrace", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_csrf_token_is_rejected_after_registration_until_refreshed()
    {
        using var client = factory.CreateClient();
        var anonymousToken = await GetCsrf(client);
        var registration = await SendWithToken(client, HttpMethod.Post, "/api/auth/register", anonymousToken, new
        {
            displayName = "Stale Token Owner",
            email = $"stale-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var auth = await registration.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);

        var staleCreate = await SendWithToken(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", anonymousToken, new { name = "Stale Project", type = "Business" });
        Assert.Equal(HttpStatusCode.BadRequest, staleCreate.StatusCode);
        var staleBody = await staleCreate.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<JsonElement>(staleBody);
        Assert.Equal("CSRF_VALIDATION_FAILED", error.GetProperty("error").GetProperty("code").GetString());

        var refreshedToken = await GetCsrf(client);
        var project = await SendWithToken<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", refreshedToken, new { name = "Fresh Project", description = "1", type = "Business" });
        Assert.Equal("Fresh Project", project.Name);
    }

    [Fact]
    public async Task Login_logout_login_and_multiple_project_operations_work_with_refreshed_tokens()
    {
        using var client = factory.CreateClient();
        var email = $"lifecycle-{Guid.NewGuid():N}@example.com";
        var registration = await Register(client, "Lifecycle Owner", email);
        var auth = await registration.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        await Logout(client);

        var loginToken = await GetCsrf(client);
        var login = await SendWithToken(client, HttpMethod.Post, "/api/auth/login", loginToken, new { email, password = "StrongPassword!123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authenticatedToken = await GetCsrf(client);

        var first = await SendWithToken<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", authenticatedToken, new { name = "Lifecycle One", description = "1", type = "Business" });
        var second = await SendWithToken<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", authenticatedToken, new { name = "Lifecycle Two", description = "2", type = "Marketing" });
        var updated = await SendWithToken<ProjectDto>(client, HttpMethod.Patch, $"/api/projects/{first.Id}", authenticatedToken, new { name = "Lifecycle One Updated", description = "updated", type = "Business" });
        Assert.Equal("Lifecycle One Updated", updated.Name);
        await SendWithToken<ProjectDto>(client, HttpMethod.Post, $"/api/projects/{first.Id}/archive", authenticatedToken, null);
        await SendWithToken<ProjectDto>(client, HttpMethod.Post, $"/api/projects/{first.Id}/restore", authenticatedToken, null);
        var active = await client.GetFromJsonAsync<List<ProjectDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/projects?status=Active");
        Assert.Contains(active!, project => project.Id == first.Id);
        Assert.Contains(active!, project => project.Id == second.Id);
    }

    private async Task<HttpResponseMessage> Register(HttpClient client, string displayName, string email) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email, password = "StrongPassword!123", preferredLanguage = "en" });

    private async Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password });

    private static async Task Logout(HttpClient client)
    {
        var token = await GetCsrf(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var token = await GetCsrf(client);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithToken<T>(HttpClient client, HttpMethod method, string path, string token, object? payload)
    {
        var response = await SendWithToken(client, method, path, token, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithToken(HttpClient client, HttpMethod method, string path, string token, object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrf(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }
}
