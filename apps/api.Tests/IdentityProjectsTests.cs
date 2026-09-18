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

public sealed class TaslimApiFactory : WebApplicationFactory<Program>
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

    private static async Task<string> GetCsrf(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }
}
