using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordRecoveryRateLimitHttpAcceptanceTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Forgot_is_30_per_minute_per_connection_ip_and_ignores_forged_forwarded_for()
    {
        using var factory = new RateLimitApiFactory();
        using var client = factory.CreateClient();
        var csrf = await GetCsrfAsync(client);
        var acceptedBodies = new List<string>();
        var sourceIp = IPAddress.Parse("203.0.113.40");

        for (var index = 0; index < 30; index++)
        {
            var forgedForwardedFor = IPAddress.Parse($"198.51.100.{index + 1}").ToString();
            using var response = await SendForgotAsync(
                client,
                csrf,
                $"missing-{index}@example.test",
                sourceIp,
                forgedForwardedFor);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            acceptedBodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.All(acceptedBodies, body => Assert.Equal(acceptedBodies[0], body));

        // A different attacker-controlled X-Forwarded-For value cannot evade the
        // exhausted peer-IP bucket. This is TestServer source-header evidence only;
        // it says nothing about Railway's actual ingress/proxy configuration.
        using var exhausted = await SendForgotAsync(
            client,
            csrf,
            "missing-over-limit@example.test",
            sourceIp,
            "192.0.2.250");
        AssertRateLimited(exhausted);

        // The harness explicitly injects a different synthetic Connection.RemoteIpAddress
        // (not XFF) to prove the fixed-window partition key is the peer connection IP.
        using var otherIp = await SendForgotAsync(
            client,
            csrf,
            "missing-other-ip@example.test",
            IPAddress.Parse("203.0.113.41"),
            "192.0.2.250");
        Assert.Equal(HttpStatusCode.Accepted, otherIp.StatusCode);
        Assert.Equal(0, factory.Sender.SendCount);
    }

    [Fact]
    public async Task Forgot_concurrency_limit_is_two_and_rejects_without_queueing()
    {
        using var factory = new RateLimitApiFactory(holdForgotRequests: true);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var thirdClient = factory.CreateClient();
        var firstCsrf = await GetCsrfAsync(firstClient);
        var secondCsrf = await GetCsrfAsync(secondClient);
        var thirdCsrf = await GetCsrfAsync(thirdClient);
        var sourceIp = IPAddress.Parse("203.0.113.50");

        var first = SendForgotAsync(firstClient, firstCsrf, "concurrent-a@example.test", sourceIp, "198.51.100.1", hold: true);
        var second = SendForgotAsync(secondClient, secondCsrf, "concurrent-b@example.test", sourceIp, "198.51.100.2", hold: true);
        try
        {
            await factory.Gate.BothEntered.Task.WaitAsync(RequestTimeout);
            Assert.Equal(2, factory.Gate.Arrivals);

            var third = SendForgotAsync(thirdClient, thirdCsrf, "concurrent-c@example.test", sourceIp, "198.51.100.3");
            var completed = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(third, completed);
            using var rejected = await third;
            AssertRateLimited(rejected);
            Assert.Equal(2, factory.Gate.Arrivals);

            // The first two remain held while the third is already 429. Thus it was
            // rejected immediately, rather than queued behind either active request.
            Assert.False(factory.Gate.IsReleased);
        }
        finally
        {
            factory.Gate.Release();
        }

        using var firstResponse = await first.WaitAsync(RequestTimeout);
        using var secondResponse = await second.WaitAsync(RequestTimeout);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.Equal(0, factory.Sender.SendCount);
    }

    [Fact]
    public async Task Known_unknown_and_repeat_cooldown_requests_are_response_uniform()
    {
        using var factory = new RateLimitApiFactory();
        var email = $"rate-known-{Guid.NewGuid():N}@example.test";
        await CreateSyntheticUserAsync(factory, email);

        using var client = factory.CreateClient();
        var csrf = await GetCsrfAsync(client);
        var sourceIp = IPAddress.Parse("203.0.113.60");

        using var known = await SendForgotAsync(client, csrf, email, sourceIp, "198.51.100.10");
        using var unknown = await SendForgotAsync(client, csrf, "not-found@example.test", sourceIp, "198.51.100.11");
        using var repeated = await SendForgotAsync(client, csrf, email, sourceIp, "198.51.100.12");

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        var knownBody = await known.Content.ReadAsStringAsync();
        Assert.Equal(knownBody, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(knownBody, await repeated.Content.ReadAsStringAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.PasswordResetEmailOutbox.CountAsync(entry => entry.UserId != Guid.Empty));
        Assert.Equal(0, factory.Sender.SendCount);
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendForgotAsync(
        HttpClient client,
        string csrf,
        string email,
        IPAddress sourceIp,
        string forwardedFor,
        bool hold = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/forgot")
        {
            Content = JsonContent.Create(new { email }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("X-Test-Remote-IP", sourceIp.ToString());
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (hold) request.Headers.Add("X-Test-Hold-Forgot", "true");
        return await client.SendAsync(request).WaitAsync(RequestTimeout);
    }

    private static void AssertRateLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal("60", Assert.Single(values!));
    }

    private static async Task CreateSyntheticUserAsync(RateLimitApiFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Synthetic Rate Limit User",
            PreferredLanguage = "en",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true,
        };
        var result = await userManager.CreateAsync(user, "StrongPassword!123");
        Assert.True(result.Succeeded, "Synthetic user creation failed; identity error details omitted.");
    }

    private sealed class RateLimitApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-rate-limit-{Guid.NewGuid():N}.sqlite");
        private readonly string connectionString;
        private readonly bool holdForgotRequests;
        private readonly NoOpEmailSender sender = new();
        private readonly RequestConcurrencyGate gate = new();

        public NoOpEmailSender Sender => sender;
        public RequestConcurrencyGate Gate => gate;

        public RateLimitApiFactory(bool holdForgotRequests = false)
        {
            this.holdForgotRequests = holdForgotRequests;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 15,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ApplyMigrations"] = "false",
                    ["PasswordRecovery:Enabled"] = "true",
                    ["PasswordRecovery:PublicBaseUrl"] = "http://localhost:3000",
                    ["PasswordRecovery:RequestCooldownMinutes"] = "5",
                    ["PasswordRecovery:WorkerPollSeconds"] = "1",
                    ["PasswordRecovery:MaxDeliveryAttempts"] = "5",
                    ["PasswordRecovery:LeaseSeconds"] = "60",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connectionString));
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
                services.AddSingleton(gate);
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestConnectionAddressStartupFilter>();

                if (holdForgotRequests)
                    services.Configure<MvcOptions>(options => options.Filters.Add(new HoldForgotActionFilter(gate)));

                var worker = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PasswordResetEmailWorker));
                if (worker is not null) services.Remove(worker);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class NoOpEmailSender : IEmailSender
    {
        private int sendCount;
        public bool IsConfigured => true;
        public int SendCount => Volatile.Read(ref sendCount);

        public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref sendCount);
            return Task.CompletedTask;
        }
    }

    private sealed class RequestConcurrencyGate
    {
        private int arrivals;
        private int released;
        private readonly TaskCompletionSource<bool> bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> BothEntered => bothEntered;
        public int Arrivals => Volatile.Read(ref arrivals);
        public bool IsReleased => Volatile.Read(ref released) != 0;

        public async Task HoldAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothEntered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            Volatile.Write(ref released, 1);
            release.TrySetResult(true);
        }
    }

    private sealed class HoldForgotActionFilter(RequestConcurrencyGate gate) : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (HttpMethods.IsPost(context.HttpContext.Request.Method)
                && context.HttpContext.Request.Path.Equals("/api/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
                && context.HttpContext.Request.Headers.ContainsKey("X-Test-Hold-Forgot"))
                await gate.HoldAsync(context.HttpContext.RequestAborted);

            await next();
        }
    }

    private sealed class TestConnectionAddressStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Remote-IP", out var values)
                    && IPAddress.TryParse(values.ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;

                await nextMiddleware();
            });
            next(app);
        };
    }
}
