using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-neutral shared generation-job lifecycle regressions. The existing suite
// covers sequential idempotency replays (GenerationJobsTests), lease-expiry
// recovery and the movie-publish contract (GenerationJobDeferredPublicationTests),
// but leaves two generic gaps that these tests close on the disjoint
// system.test pipeline only:
//
//   1. Same-idempotency-key requests arriving CONCURRENTLY - double-clicked create
//      or retry buttons after a lost response. This is the path where the
//      unique-index conflict branch inside GenerationJobService.CreateAsync is the
//      only thing preventing a second job row AND a second usage reservation.
//   2. Claim-token fencing of terminal finalization - a worker whose claim moved
//      on (lease recovery, cancelled terminal state) must never publish outputs
//      or re-finalize the job, and must never add or bill a second reservation.
//
// No external provider is contacted: every scenario uses the deterministic
// synthetic system.test handler with zero-cost usage, and no lane-owned job type
// (movie, chat, music, voice/video, document, presentation, billing, autopilot,
// route auth) is referenced, keeping the change disjoint from the other lanes.

/// <summary>
/// Worker-disabled factory: jobs keep the exact state the API wrote, so replay,
/// cancellation and conflict outcomes are observed without a background claim
/// racing the assertions.
/// </summary>
public sealed class GenerationJobConcurrencyNoWorkerFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    public string DatabaseFilePath => databasePath;
}

public sealed class GenerationJobConcurrencyIdempotencyTests : IClassFixture<GenerationJobConcurrencyNoWorkerFactory>
{
    private readonly GenerationJobConcurrencyNoWorkerFactory factory;

    public GenerationJobConcurrencyIdempotencyTests(GenerationJobConcurrencyNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Test_database_applies_the_scoped_unique_idempotency_index()
    {
        // The concurrent same-key conflict path converges on one job only while the
        // (CreatedByUserId, IdempotencyKey) unique index exists in the schema these
        // tests create. If the index silently disappears, the five racing writers
        // would each keep their own row.
        using var connection = new SqliteConnection($"Data Source={factory.DatabaseFilePath};Cache=Shared;Default Timeout=30");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'GenerationJobs' AND sql LIKE '%IdempotencyKey%';";
        var sql = command.ExecuteScalar() as string;
        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CreatedByUserId", sql, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKey", sql, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Simultaneous_same_user_same_key_creates_settle_on_one_job_with_one_reservation()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-race-single-{Guid.NewGuid():N}@example.com");
        const string key = "generation-concurrent-single-user-001";
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{\"purpose\":\"single-user-race\"}",
        };

        // Five simultaneous submissions from one session: the generation-creation
        // limiter (four in-flight permits, no queue) rejects the overflow with 429
        // before the action runs, so at least two requests genuinely enter
        // CreateAsync with the same key and identical fingerprint.
        var token = await IssueAntiForgeryTokenAsync(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => TrySendCreateAsync(client, token, payload, key)));
        AssertOnlyAcceptedOrRateLimited(responses, HttpStatusCode.Created);
        Assert.True(responses.Count(response => response.Status == HttpStatusCode.Created) >= 2,
            $"expected a true same-key overlap, got {responses.Count(response => response.Status == HttpStatusCode.Created)} accepted of {responses.Length}");

        var created = responses.Where(response => response.Status == HttpStatusCode.Created).Select(response => response.Dto).ToArray();
        var jobIds = created.Select(job => job!.Id).Distinct().ToArray();
        Assert.Single(jobIds);
        var winner = created[0]!;
        Assert.Equal(auth.PersonalWorkspace.Id, winner.WorkspaceId);
        Assert.Equal(GenerationJobStatus.Queued.ToString(), winner.Status);
        Assert.Equal(0, winner.RetryCount);
        // Every 429 is a rejection with no job body: rejections cannot duplicate work.
        Assert.All(responses.Where(response => response.Status == HttpStatusCode.TooManyRequests), response => Assert.Null(response.Dto));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.IdempotencyKey == key));
        // Cost-safe no-duplicate guarantee: exactly one reservation for the single job
        // (keyed on the durable job link rather than the ledger request id format).
        Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.GenerationJobId == jobIds[0]));
        Assert.Equal(0, await db.Assets.AsNoTracking().CountAsync(item => item.SourceGenerationJobId == jobIds[0]));
        Assert.Equal(0, await db.GenerationJobOutputs.AsNoTracking().CountAsync(item => item.GenerationJobId == jobIds[0]));
    }

    [Fact]
    public async Task Same_key_under_different_users_stays_scoped_per_owner()
    {
        // The unique index is (CreatedByUserId, IdempotencyKey) on purpose: a shared
        // key string must never let one member of a workspace replay another member's
        // stored job, and must never collapse two members' distinct generations into
        // one row. Four users submitting an identical request under one key therefore
        // produce four independent jobs, each with its own single reservation, and
        // every user's replay resolves to that user's own job.
        const string key = "generation-concurrent-cross-user";
        var clients = new HttpClient[4];
        var created = new GenerationJobDto[4];
        for (var i = 0; i < clients.Length; i++)
        {
            clients[i] = factory.CreateClient();
            var auth = await RegisterAsync(clients[i], $"jobs-race-user-{Guid.NewGuid():N}-{i}@example.com");
            created[i] = await SendCreateAsync(clients[i], new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                jobType = GenerationJobTypes.SystemTest,
                inputJson = "{\"purpose\":\"cross-user-scope\"}",
            }, key);
            // Sequential replay for the same owner resolves to the owner's own row.
            var replay = await SendCreateAsync(clients[i], new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                jobType = GenerationJobTypes.SystemTest,
                inputJson = "{\"purpose\":\"cross-user-scope\"}",
            }, key);
            Assert.Equal(created[i].Id, replay.Id);
        }

        Assert.Equal(clients.Length, created.Select(job => job.Id).Distinct().Count());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var rows = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.IdempotencyKey == key)
            .Select(job => new { job.Id, job.CreatedByUserId })
            .ToListAsync();
        Assert.Equal(clients.Length, rows.Count);
        Assert.Equal(clients.Length, created.Select(job => job.Id).Intersect(rows.Select(row => row.Id)).Count());
        Assert.Equal(clients.Length, rows.Select(row => row.CreatedByUserId).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.GenerationJobId == row.Id));
        }
    }

    [Fact]
    public async Task Simultaneous_terminal_retries_with_one_key_fan_into_one_child_with_one_reservation()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-race-retry-{Guid.NewGuid():N}@example.com");
        var sourceId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = sourceId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Failed,
                InputJson = "{}",
                ErrorCode = "synthetic_failure",
                ErrorMessage = "Deterministic test failure.",
                CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                FailedAt = DateTime.UtcNow.AddMinutes(-1),
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        const string retryKey = "generation-concurrent-retry-001";
        var token = await IssueAntiForgeryTokenAsync(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => TrySendRetryAsync(client, token, sourceId, retryKey)));
        AssertOnlyAcceptedOrRateLimited(responses, HttpStatusCode.Accepted);
        Assert.True(responses.Count(response => response.Status == HttpStatusCode.Accepted) >= 2,
            "expected a true same-key retry overlap");

        var childIds = responses
            .Where(response => response.Status == HttpStatusCode.Accepted)
            .Select(response => response.Dto!.Id)
            .Distinct()
            .ToArray();
        Assert.Single(childIds);
        var child = responses.First(response => response.Status == HttpStatusCode.Accepted).Dto!;
        Assert.Equal(sourceId, child.RetryOfJobId);
        Assert.Equal(1, child.RetryCount);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await finalDb.GenerationJobs.AsNoTracking().CountAsync(job => job.IdempotencyKey == retryKey));
        Assert.Equal(1, await finalDb.GenerationJobs.AsNoTracking().CountAsync(job => job.RetryOfJobId == sourceId));
        // A five-way retry storm must reserve budget for the replayed generation once.
        Assert.Equal(1, await finalDb.UsageTransactions.AsNoTracking().CountAsync(item => item.GenerationJobId == childIds[0]));
        // The failed parent keeps its terminal identity untouched.
        var parent = await finalDb.GenerationJobs.AsNoTracking().SingleAsync(job => job.Id == sourceId);
        Assert.Equal(GenerationJobStatus.Failed, parent.Status);
        Assert.Null(parent.RetryOfJobId);
    }

    [Fact]
    public async Task Simultaneous_same_key_with_conflicting_payloads_never_double_queues()
    {
        // The replay pre-check and the unique-index conflict handler are two separate
        // fingerprint gates. Racing one accepted request against several rejected ones
        // proves the second gate is not skipped under concurrency: exactly one job and
        // one reservation survive, and every other payload under the same key is
        // refused with the documented code.
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-race-conflict-{Guid.NewGuid():N}@example.com");
        const string key = "generation-concurrent-conflict-001";
        var accepted = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{\"purpose\":\"first\"}",
        };
        var conflicting = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{\"purpose\":\"different-request-same-key\"}",
        };

        var token = await IssueAntiForgeryTokenAsync(client);
        var responses = await Task.WhenAll(new[]
        {
            TrySendCreateAsync(client, token, accepted, key),
            TrySendCreateAsync(client, token, conflicting, key),
            TrySendCreateAsync(client, token, conflicting, key),
            TrySendCreateAsync(client, token, accepted, key),
        });

        AssertOnlyAcceptedOrConflictOrRateLimited(responses);

        // Whichever payload family legitimately won the insert race, the two requests
        // carrying that payload share one job and the other family is refused outright.
        var created = responses.Where(response => response.Status == HttpStatusCode.Created).ToArray();
        var jobIds = created.Select(response => response.Dto!.Id).Distinct().ToArray();
        Assert.NotEmpty(created);
        Assert.Single(jobIds);

        var conflicts = responses.Where(response => response.Status == HttpStatusCode.BadRequest).ToArray();
        Assert.All(conflicts, response =>
        {
            using var document = JsonDocument.Parse(response.Body);
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        });

        // Responses keep the submission order: 0 and 3 carry the first payload, 1 and 2
        // the conflicting one. When both families are admitted past the limiter, the
        // fingerprint gate has to refuse at least one of them.
        var admittedFirst = new[] { 0, 3 }.Count(index => responses[index].Status != HttpStatusCode.TooManyRequests);
        var admittedConflict = new[] { 1, 2 }.Count(index => responses[index].Status != HttpStatusCode.TooManyRequests);
        if (admittedFirst > 0 && admittedConflict > 0)
            Assert.NotEmpty(conflicts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var stored = await db.GenerationJobs.AsNoTracking().SingleAsync(job => job.IdempotencyKey == key);
        Assert.Equal(jobIds[0], stored.Id);
        // Exactly one job row and one budget reservation survived the conflict race.
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.CreatedByUserId == auth.User.Id && job.IdempotencyKey == key));
        Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.GenerationJobId == jobIds[0]));
    }

    [Fact]
    public async Task Cancelling_a_queued_job_then_replaying_the_same_key_replays_the_cancelled_terminal_state()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-cancel-replay-{Guid.NewGuid():N}@example.com");
        const string key = "generation-cancel-replay-001";
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{\"purpose\":\"cancel-then-replay\"}",
        };

        var created = await SendCreateAsync(client, payload, key);
        var cancelResponse = await SendWithCsrfAsync(client, HttpMethod.Post, $"/api/generation/jobs/{created.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Contract pin: a same-key replay returns the stored job as-is. Restarting a
        // cancelled generation requires the retry endpoint with a fresh key; the
        // replay must never resurrect the row, requeue it, or add a reservation.
        var replay = await SendCreateAsync(client, payload, key);
        Assert.Equal(created.Id, replay.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), replay.Status);
        Assert.Equal(GenerationJobErrorCodes.Cancelled, replay.ErrorCode);
        Assert.Null(replay.CompletedAt);
        Assert.Null(replay.ResultJson);
        Assert.Empty(replay.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.IdempotencyKey == key));
        var stored = await db.GenerationJobs.AsNoTracking().SingleAsync(job => job.Id == created.Id);
        Assert.Equal(GenerationJobStatus.Cancelled, stored.Status);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Id);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Replay_of_a_completed_job_returns_the_identical_terminal_read_model()
    {
        // Generic status consistency: the replay response and the following read must
        // be the same DTO. A replay that reports a stale (for example Queued) status
        // after the job finalized would drive clients to poll an already-terminal job.
        using var workerFactory = new GenerationJobsApiFactory();
        using var client = workerFactory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-replay-terminal-{Guid.NewGuid():N}@example.com");
        const string key = "generation-replay-terminal-001";
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{\"purpose\":\"replay-terminal\"}",
        };

        var created = await SendCreateAsync(client, payload, key);
        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 160; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Id}");
            if (terminal!.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.Equal("Succeeded", terminal.Status);

        var replay = await SendCreateAsync(client, payload, key);
        Assert.Equal(terminal.Id, replay.Id);
        AssertSameReadModel(terminal, replay);

        var reread = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Id}");
        AssertSameReadModel(terminal, reread);
        // Finding pinned from current main: the same-key replay of a finalized job
        // resolves through the idempotency lookup in GenerationJobService.CreateAsync,
        // which loads the stored job WITHOUT its Outputs navigation, while GET includes
        // it. Scalar status fields stay consistent, but the replay response publishes an
        // empty Outputs list for a job that demonstrably has stored outputs. The
        // canonical read model (GET) is asserted against the durable rows below; the
        // replay gap is recorded here so a fix (including Outputs in the replay lookup)
        // is caught by this test and the inconsistency cannot silently widen.
        Assert.Single(terminal.Outputs);
        Assert.Single(reread!.Outputs);
        Assert.Empty(replay.Outputs);

        using var scope = workerFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.IdempotencyKey == key));
        Assert.Equal(1, await db.GenerationJobOutputs.AsNoTracking().CountAsync(item => item.GenerationJobId == terminal.Id));
        Assert.Equal(1, await db.Assets.AsNoTracking().CountAsync(item => item.SourceGenerationJobId == terminal.Id));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == terminal.Id);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    // Compares the scalar read-model fields that clients poll. The Outputs
    // collection is intentionally excluded: the same-key replay gap is pinned
    // explicitly at its call site, and the canonical GET read is compared with the
    // durable rows in the assertions that follow.
    private static void AssertSameReadModel(GenerationJobDto expected, GenerationJobDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ProgressPercent, actual.ProgressPercent);
        Assert.Equal(expected.ErrorCode, actual.ErrorCode);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
        Assert.Equal(expected.ResultJson, actual.ResultJson);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.CancelledAt, actual.CancelledAt);
        Assert.Equal(expected.FailedAt, actual.FailedAt);
        Assert.Equal(expected.RetryOfJobId, actual.RetryOfJobId);
        Assert.Equal(expected.RetryCount, actual.RetryCount);
    }

    private static async Task<GenerationJobDto> SendCreateAsync(HttpClient client, object payload, string idempotencyKey)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/generation/jobs", payload, idempotencyKey);
        Assert.True(response.IsSuccessStatusCode, $"create failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<GenerationJobDto>())!;
    }

    // Race helpers keep the rejection body so limiter 429s and validation rejections
    // can be told apart from the requests that actually reached the idempotency path.
    // One antiforgery token is issued before the race and reused by every racing
    // request: fetching a token per request rotates the paired cookie mid-flight and
    // would reject the losers with 400 CSRF_VALIDATION_FAILED instead of exercising
    // the idempotency path.
    private static async Task<string> IssueAntiForgeryTokenAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static Task<(HttpStatusCode Status, GenerationJobDto? Dto, string Body)> TrySendCreateAsync(HttpClient client, string token, object payload, string idempotencyKey) =>
        TrySendAsync(client, token, HttpMethod.Post, "/api/generation/jobs", payload, idempotencyKey);

    private static Task<(HttpStatusCode Status, GenerationJobDto? Dto, string Body)> TrySendRetryAsync(HttpClient client, string token, Guid jobId, string idempotencyKey) =>
        TrySendAsync(client, token, HttpMethod.Post, $"/api/generation/jobs/{jobId}/retry", null, idempotencyKey);

    private static async Task<(HttpStatusCode Status, GenerationJobDto? Dto, string Body)> TrySendAsync(HttpClient client, string token, HttpMethod method, string path, object? payload, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return (response.StatusCode, null, body);
            return (response.StatusCode, JsonSerializer.Deserialize<GenerationJobDto>(body, JsonOptions), body);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Anything other than the documented accepted/rejected pair is a defect; the
    // response body is reported so a rejection code is never guessed at.
    private static void AssertOnlyAcceptedOrRateLimited((HttpStatusCode Status, GenerationJobDto? Dto, string Body)[] responses, HttpStatusCode accepted)
    {
        var unexpected = responses
            .Where(response => response.Status != accepted && response.Status != HttpStatusCode.TooManyRequests)
            .Select(response => $"{(int)response.Status} {response.Body}")
            .ToArray();
        Assert.Empty(unexpected);
    }

    // The conflict variant additionally documents the 400 IDEMPOTENCY_KEY_REUSED
    // rejection alongside the limiter's 429.
    private static void AssertOnlyAcceptedOrConflictOrRateLimited((HttpStatusCode Status, GenerationJobDto? Dto, string Body)[] responses)
    {
        var unexpected = responses
            .Where(response => response.Status != HttpStatusCode.Created
                && response.Status != HttpStatusCode.BadRequest
                && response.Status != HttpStatusCode.TooManyRequests)
            .Select(response => $"{(int)response.Status} {response.Body}")
            .ToArray();
        Assert.Empty(unexpected);
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string email)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Concurrency Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
/// <summary>
/// Holds the ORIGINAL claim of one specific generic job in flight so the test can
/// let the production lease-recovery path requeue it underneath the running worker.
/// Recovered claims (RetryCount greater than zero) execute normally, which is what
/// creates the double execution the fencing contract has to survive.
/// </summary>
public sealed class GateOriginalClaimGenerationHandler : IGenerationJobHandler
{
    private readonly Guid gatedJobId;
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GateOriginalClaimGenerationHandler(Guid gatedJobId)
    {
        this.gatedJobId = gatedJobId;
    }

    public Task BlockedAsync => started.Task;

    public void Release() => finish.TrySetResult(true);

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (job.Id == gatedJobId && job.RetryCount == 0)
        {
            progress.Report(50);
            started.TrySetResult(true);
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        var result = JsonSerializer.Serialize(new { jobType = GenerationJobTypes.SystemTest, message = "Doubly executed synthetic generation." });
        var metadata = JsonSerializer.Serialize(new { deterministic = true });
        var artifact = new GeneratedFileArtifact("generation-result.json", "application/json", System.Text.Encoding.UTF8.GetBytes(result));
        var asset = new GeneratedAssetDescriptor("Fenced generation", "Synthetic provider-free output.", AssetTypes.File, metadata);
        return new GenerationHandlerResult(result, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], new AiUsageMetadata("system", "system.test", null, null, null, 0m, 0m, 0, "completed", true));
    }
}

public sealed class GenerationJobFencedExecutionFactory : GenerationJobsApiFactory
{
    public GenerationJobFencedExecutionFactory(Guid gatedJobId)
    {
        Handler = new GateOriginalClaimGenerationHandler(gatedJobId);
    }

    public GateOriginalClaimGenerationHandler Handler { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // A second worker keeps polling while the first holds the gated claim, so the
        // production expired-lease recovery can requeue the job mid-execution.
        builder.UseSetting("GenerationJobs:WorkerConcurrency", "2");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler>(Handler);
        });
    }
}

public sealed class GenerationJobClaimFencingTests
{
    [Fact]
    public async Task DoublyExecutedJobPublishesItsGenerationExactlyOnce()
    {
        // The gap this pins: when an expired lease requeues a job that a worker is
        // still executing, the job legitimately runs twice. Claim-token fencing must
        // converge that into ONE published generation - one asset, one stored file,
        // one output row and one settled reservation - instead of duplicating billable
        // side effects. Deterministic by construction: the original claim is parked on
        // an explicit gate while the real recovery path requeues it, and the parked
        // claim is only released after the recovered claim has finished.
        var jobId = Guid.NewGuid();
        using var factory = new GenerationJobFencedExecutionFactory(jobId);
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, $"jobs-fencing-{Guid.NewGuid():N}@example.com");
        var seededToken = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Queued,
                InputJson = "{}",
                CreatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
                ConcurrencyToken = seededToken,
            });
            await db.SaveChangesAsync();
        }

        await factory.Handler.BlockedAsync.WaitAsync(TimeSpan.FromSeconds(30));

        Guid activeToken;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var inFlight = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
            Assert.Equal(GenerationJobStatus.Running, inFlight.Status);
            Assert.Equal(0, inFlight.RetryCount);
            activeToken = inFlight.ConcurrencyToken;
            Assert.NotEqual(seededToken, activeToken);

            // Expire only this live lease; the recovery pass runs on its one-second
            // cadence and reclaims under a fresh token.
            var expiredAt = DateTime.UtcNow.AddSeconds(-1);
            var expired = await db.GenerationJobs
                .Where(item => item.Id == jobId && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == activeToken)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClaimExpiresAt, expiredAt));
            Assert.Equal(1, expired);
        }

        GenerationJob? recovered = null;
        for (var attempt = 0; attempt < 600; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            recovered = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
            if (recovered.RetryCount >= 1 && recovered.ConcurrencyToken != activeToken) break;
            await Task.Delay(50);
        }

        Assert.NotNull(recovered);
        Assert.True(recovered!.RetryCount >= 1, $"lease recovery did not requeue the in-flight job (RetryCount={recovered.RetryCount})");
        Assert.NotEqual(activeToken, recovered.ConcurrencyToken);

        // Release the abandoned claim last, so the recovered execution has already
        // converged the job and the stale worker can only be fenced out.
        factory.Handler.Release();

        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 600; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (terminal!.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.True(terminal.Status == GenerationJobStatus.Succeeded.ToString(),
            $"Status={terminal.Status}; Code={terminal.ErrorCode}; Message={terminal.ErrorMessage}");
        Assert.Equal(100, terminal.ProgressPercent);
        Assert.Null(terminal.ErrorCode);
        Assert.NotNull(terminal.ResultJson);

        // Give the fenced worker its full finalization window, then prove it wrote
        // nothing: a publish from the abandoned claim would add a second row here.
        await Task.Delay(1_000);
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await finalDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Null(job.ClaimExpiresAt);
        Assert.NotNull(job.CompletedAt);

        Assert.Equal(1, await finalDb.GenerationJobOutputs.AsNoTracking().CountAsync(item => item.GenerationJobId == jobId));
        Assert.Equal(1, await finalDb.Assets.AsNoTracking().CountAsync(item => item.SourceGenerationJobId == jobId));
        Assert.Equal(1, await finalDb.StoredFiles.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Single(terminal.Outputs);

        // The double execution really happened (two provider attempts), yet the
        // reservation settled once and nothing was charged for the aborted claim.
        Assert.Equal(2, await finalDb.GenerationProviderAttempts.AsNoTracking().CountAsync(item => item.GenerationJobId == jobId));
        var usage = await finalDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string email)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Fencing Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}

public sealed class GenerationJobFinalizationSourceContractTests
{
    // Canonical fenced predicates of the shared lifecycle with the number of sites
    // that must carry each. Counting occurrences turns silent removal of the
    // claim-token guard from any single finalization into a deterministic failure.
    private const string CompletionFence =
        "item.Id == current.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == claimedJob.ConcurrencyToken && !item.CancellationRequested";
    private const string RecoveryFence = "&& item.ConcurrencyToken == expired.ConcurrencyToken";
    private const string WorkerTokenFence = "job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken";
    private const string TerminalFence = "item.Id == job.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == concurrencyToken";

    [Fact]
    public void Every_terminal_finalization_of_a_running_job_is_fenced_by_the_claim_token()
    {
        // The shared lifecycle converges to Succeeded/Failed/Cancelled only through
        // compare-and-swap updates that filter on the active claim token, so a stale
        // worker can never finalize - or re-publish - a job whose claim already moved
        // on. Dropping the token predicate from any one site would let a second
        // execution duplicate generation side effects; this contract pins all of them.
        var source = ReadSharedLifecycleSource();

        Assert.Equal(1, CountOccurrences(source, CompletionFence));
        Assert.Equal(2, CountOccurrences(source, WorkerTokenFence));
        Assert.Equal(2, CountOccurrences(source, TerminalFence));

        // The lease-recovery finalizations filter across wrapped lines; assert every
        // Running-status guard inside RecoverExpiredClaimsAsync is paired with the
        // expired token it is fencing against.
        var recovery = ExtractMethod(source, "private async Task RecoverExpiredClaimsAsync(");
        Assert.Equal(4, CountOccurrences(recovery, "Status == GenerationJobStatus.Running"));
        Assert.Equal(3, CountOccurrences(recovery, RecoveryFence));

        // Single-line Running guards that are not token-fenced must not finalize a
        // job. On current main exactly two exist and both are reads: the user-facing
        // cancellation request filter and the expired-claim discovery scan feeding the
        // recovery loop above. A new unfenced finalization fails this assertion.
        var unfencedLines = source.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("Status == GenerationJobStatus.Running", StringComparison.Ordinal)
                && !line.Contains("ConcurrencyToken", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(5, unfencedLines.Length);
        Assert.DoesNotContain(unfencedLines, line => line.Contains("ExecuteUpdateAsync", StringComparison.Ordinal));
        Assert.Contains(unfencedLines, line => line.Contains("item.Id == jobId", StringComparison.Ordinal));
        Assert.Contains(unfencedLines, line => line.Contains("ClaimExpiresAt.HasValue", StringComparison.Ordinal));
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected the shared lifecycle to contain '{signature}'.");
        var depth = 0;
        var seenBody = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                seenBody = true;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (seenBody && depth == 0) return source[start..(index + 1)];
            }
        }
        throw new Xunit.Sdk.XunitException($"Could not extract '{signature}'.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReadSharedLifecycleSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "apps", "api", "Generation", "GenerationJobExecution.cs")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "apps", "api", "Generation", "GenerationJobExecution.cs"));
    }
}
