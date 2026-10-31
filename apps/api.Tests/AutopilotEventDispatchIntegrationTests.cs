using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// End-to-end acceptance for the unclaimed HTTP intake/watchdog integration.
/// It uses only an in-memory SQLite database and a fake launch provider.
/// </summary>
public sealed class AutopilotEventDispatchIntegrationTests
{
    private const string SigningSecret = "autopilot-event-dispatch-integration-secret";
    private const string WaveKey = "wave-immediate-dispatch";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task Signed_completion_wakes_watchdog_and_launches_replacement_without_hourly_wait()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            ConfigureLiveFakePosture(factory);
            await EnsureDatabaseAndApproveOneBacklogItemAsync(factory, "backlog-immediate-1");

            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var payload = CompletionPayload();
                var eventId = $"evt-immediate-{Guid.NewGuid():N}";
                using var first = await PostSignedEventAsync(client, payload, eventId);
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

                // An identical delivery must wake/re-drive the durable queue, but
                // cannot create a second replacement task.
                using var duplicate = await PostSignedEventAsync(client, payload, eventId);
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                var duplicateBody = JsonSerializer.Deserialize<JsonElement>(await duplicate.Content.ReadAsStringAsync());
                Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicateBody.GetProperty("status").GetString());

                var launched = await factory.Provider.LaunchReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);

                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var intake = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
                Assert.Equal(AutopilotEventStatuses.Processed, intake.Status);

                var sourceRun = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == WaveKey);
                Assert.True(sourceRun.IntegrationGatePassed);
                Assert.True(sourceRun.ReleaseGatePassed);
                Assert.Equal(AutopilotRunStates.HandoffPendingHuman, sourceRun.State);

                var replacement = await db.AutopilotWaveLaunchTasks.SingleAsync();
                Assert.Equal("backlog-immediate-1", replacement.BacklogItemKey);
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, replacement.Status);
                Assert.Single(factory.Provider.Launches);
                Assert.NotEmpty(factory.Provider.Launches.Single().IdempotencyKey);
            }
            finally
            {
                await watchdog.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Startup_recovery_processes_committed_event_if_process_died_before_wake_signal()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            ConfigureLiveFakePosture(factory);
            await EnsureDatabaseAndApproveOneBacklogItemAsync(factory, "backlog-restart-1");

            var payload = CompletionPayload();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = ComputeSignature(timestamp, payload);
            using (var scope = factory.Services.CreateScope())
            {
                var intake = scope.ServiceProvider.GetRequiredService<IAutopilotEventIntake>();
                var saved = await intake.SubmitAsync(new AutopilotIntakeRequest(
                    signature,
                    timestamp,
                    payload,
                    "completion-bridge",
                    $"evt-committed-before-crash-{Guid.NewGuid():N}",
                    AutopilotEventTypes.WaveTaskCompleted));
                Assert.Equal(AutopilotIntakeOutcomes.Accepted, saved.Outcome);
            }

            // No process-local notification was sent. Starting the watchdog is
            // equivalent to restarting after the durable commit; its first pass
            // recovers the queued row before the next-wave check.
            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var launched = await factory.Provider.LaunchReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);
                Assert.Single(factory.Provider.Launches);
            }
            finally
            {
                await watchdog.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    private static AutopilotWatchdogService CreateWatchdog(DispatchTestFactory factory) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<AutopilotOptions>>(),
            NullLogger<AutopilotWatchdogService>.Instance);

    private static void ConfigureLiveFakePosture(DispatchTestFactory factory)
    {
        var options = factory.Services.GetRequiredService<AutopilotOptions>();
        options.Enabled = true;
        options.DryRun = false;
        options.AllowNextWaveLaunch = true;
        options.AllowNextWaveLaunchWhenReleasePending = true;
        options.AllowAutomaticRelease = false;
        options.AllowAutomaticIntegrationMerge = false;
        options.ChargingEnabled = false;
        options.PaidProvidersEnabled = false;
        options.MaxTasksPerWave = 1;
        options.WatchdogIntervalMinutes = 60;
        options.Normalize();
    }

    private static async Task EnsureDatabaseAndApproveOneBacklogItemAsync(DispatchTestFactory factory, string itemKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.AutopilotBacklogItems.Add(new AutopilotBacklogItem
        {
            Id = Guid.NewGuid(),
            ItemKey = itemKey,
            Title = "Approved replacement task",
            AcceptanceSummary = "Complete the isolated integration acceptance test.",
            Kind = AutopilotBacklogItemKinds.Development,
            Approved = true,
            ApprovedBy = "integration-test-operator",
            ApprovedAt = DateTime.UtcNow,
            ApprovalNote = "Synthetic test approval only.",
            Priority = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string CompletionPayload() => JsonSerializer.Serialize(new
    {
        waveKey = WaveKey,
        taskId = "finished-task-1",
        outcome = AutopilotTaskOutcomes.Succeeded,
        branch = "feature/synthetic-wave",
        baseSha = BaseSha,
        candidateSha = CandidateSha,
        attempt = 0,
        evidence = "synthetic deterministic checks passed",
        checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [AutopilotChecks.Build] = true,
            [AutopilotChecks.UnitTests] = true,
            [AutopilotChecks.Typecheck] = true,
            [AutopilotChecks.Lint] = true,
            [AutopilotChecks.BrowserE2E] = true,
            [AutopilotChecks.Migrations] = true,
            [AutopilotChecks.Security] = true,
            [AutopilotChecks.Accounting] = true,
            [AutopilotChecks.IntegrationComplete] = true,
        },
        expectedTaskCount = 1,
        waveComplete = true,
    });

    private static async Task<HttpResponseMessage> PostSignedEventAsync(HttpClient client, string payload, string eventId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Autopilot-Source", "completion-bridge");
        content.Headers.Add("X-Autopilot-Event-Id", eventId);
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        content.Headers.Add("X-Autopilot-Timestamp", timestamp);
        content.Headers.Add("X-Autopilot-Signature", $"sha256={ComputeSignature(timestamp, payload)}");
        return await client.PostAsync("/api/autopilot/events", content);
    }

    private static string ComputeSignature(string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
    }

    private sealed class DispatchTestFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        public FakeWaveLaunchProvider Provider { get; } = new();

        public DispatchTestFactory() => connection.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddSingleton(connection);
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWaveLaunchProvider>();
                services.AddSingleton(Provider);
                services.AddScoped<IWaveLaunchProvider>(provider => provider.GetRequiredService<FakeWaveLaunchProvider>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                connection.Dispose();
        }
    }

    private sealed class FakeWaveLaunchProvider : IWaveLaunchProvider
    {
        private readonly ConcurrentQueue<WaveLaunchProviderRequest> launches = new();
        private readonly TaskCompletionSource<WaveLaunchProviderRequest> launchReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderKey => "integration-test-fake";
        public bool IsConfigured => true;
        public IReadOnlyList<WaveLaunchProviderRequest> Launches => launches.ToArray();
        public TaskCompletionSource<WaveLaunchProviderRequest> LaunchReceived => launchReceived;

        public Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default)
        {
            launches.Enqueue(request);
            launchReceived.TrySetResult(request);
            return Task.FromResult(WaveLaunchProviderResult.Launched($"fake-external-{request.TaskKey}"));
        }

        public Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WaveLaunchTaskState?>(null);
    }
}
