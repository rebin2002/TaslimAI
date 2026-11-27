using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Free-plan generation admission for a workspace is decided by two readers over the same
/// <see cref="UsageTransaction"/> rows: <see cref="UsageCostControl"/> (the studio preflight that
/// answers 429 before any job exists) and <see cref="GenerationCostGuardrailService"/> (the
/// internal safety ceilings inside job creation). If the two disagree about which terminal statuses
/// carry real provider spend, a workspace can be admitted on one path and denied on the other, or
/// re-spend cost it has already burned.
///
/// Every lane here is provider-free and charge-free: the deterministic image provider is the only
/// provider registered, the worker is disabled so an admitted job stays inert, and customer
/// charging stays at zero. No provider is ever called and nothing is billed.
/// </summary>
public sealed class FreeTierUsageAdmissionContractTests
{
    private const UsageFeature GatedFeature = UsageFeature.Image;

    // ---------------------------------------------------------------------
    // Refunded work is attributed provider spend. A refund returns the
    // customer charge, not the cost already burned at the provider, so it
    // must keep counting against the workspace ceiling exactly like
    // completed and failed work.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Refunded_transaction_with_known_provider_cost_still_counts_toward_the_daily_ceiling()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        await SeedRefundedAsync(db, workspaceId, userId, "generation:refunded-known", providerCostKnown: true, providerCostUsd: 0.40m);

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            DailyWorkspaceProviderCostCeilingUsd = 0.50m,
        });
        var result = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.20m);

        // 0.40 already spent at the provider plus 0.20 requested is over the 0.50 ceiling.
        Assert.False(result.Allowed);
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", result.RejectionCode);
    }

    [Fact]
    public async Task Refunded_transaction_with_known_provider_cost_still_counts_toward_the_monthly_ceiling()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        await SeedRefundedAsync(db, workspaceId, userId, "generation:refunded-monthly", providerCostKnown: true, providerCostUsd: 0.90m);

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            MonthlyWorkspaceProviderCostCeilingUsd = 1m,
        });
        var result = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.20m);

        Assert.False(result.Allowed);
        Assert.Equal("MONTHLY_COST_CEILING_EXCEEDED", result.RejectionCode);
    }

    [Fact]
    public async Task Both_admission_readers_report_the_same_refunded_workspace_spend()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        await SeedRefundedAsync(db, workspaceId, userId, "generation:refunded-parity", providerCostKnown: true, providerCostUsd: 0.30m);

        var guardrails = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            WorkspaceInternalSafetyCeilingUsd = 10m,
        }));
        var preview = await guardrails.EvaluateAsync(userId, workspaceId, null, KnownEstimate(0.05m));
        Assert.Equal(0.30m, preview.WorkspaceCap!.UsedUsd);

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            DailyWorkspaceProviderCostCeilingUsd = 0.30m,
        });
        var atCeiling = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.05m);

        // The studio reader must see exactly what the internal reader already reports.
        Assert.False(atCeiling.Allowed);
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", atCeiling.RejectionCode);
    }

    [Fact]
    public async Task Refunded_transaction_with_unknown_provider_cost_fails_closed_for_both_readers()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        await SeedRefundedAsync(db, workspaceId, userId, "generation:refunded-unknown", providerCostKnown: false);

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            DailyWorkspaceProviderCostCeilingUsd = 1m,
        });
        var preflight = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.20m);

        var guardrails = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            RejectUnknownEstimates = true,
            WorkspaceInternalSafetyCeilingUsd = 1m,
        }));
        var preview = await guardrails.EvaluateAsync(userId, workspaceId, null, KnownEstimate(0.20m));

        // An unattributable provider cost is unusable exposure for both readers, so admission
        // fails closed instead of guessing that the spend was zero.
        Assert.False(preflight.Allowed);
        Assert.Equal("WORKSPACE_COST_UNKNOWN", preflight.RejectionCode);
        Assert.False(preview.Allowed);
        Assert.Equal("WORKSPACE_COST_UNKNOWN", preview.RejectionCode);
    }

    [Fact]
    public async Task Cancelled_transaction_stays_out_of_both_the_ceiling_sum_and_the_exposure_set()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        db.UsageTransactions.Add(NewTransaction(
            workspaceId, userId, "generation:cancelled-boundary", UsageTransactionStatus.Cancelled,
            providerCostUsd: 0m, providerCostKnown: false));
        await db.SaveChangesAsync();

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            DailyWorkspaceProviderCostCeilingUsd = 0.50m,
        });
        var result = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.20m);

        // Cancelled work never reached a provider, so it is neither spend nor unusable exposure.
        Assert.True(result.Allowed);
        Assert.Null(result.RejectionCode);
        Assert.Equal(1, await db.UsageTransactions.CountAsync());
        Assert.Equal(UsageTransactionStatus.Cancelled, await db.UsageTransactions.Select(item => item.Status).SingleAsync());
    }

    // ---------------------------------------------------------------------
    // Deny before provider: an admission decision is a pure read. It must not
    // leave a reservation, a job or a credit movement behind.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Denied_preflight_writes_no_reservation_no_job_and_no_charge()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, _) = await AddOwnerAsync(db);

        var control = CreateCostControl(db, new UsageControlOptions
        {
            GuardrailsEnabled = true,
            RejectUnknownEstimates = true,
            MaxEstimatedProviderCostPerGenerationUsd = 0.10m,
            DailyWorkspaceProviderCostCeilingUsd = 0.05m,
        });

        var unknownEstimate = await control.CheckPreflightAsync(workspaceId, GatedFeature, null);
        Assert.False(unknownEstimate.Allowed);
        Assert.Equal("COST_ESTIMATE_UNKNOWN", unknownEstimate.RejectionCode);

        var overLimit = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.90m);
        Assert.False(overLimit.Allowed);
        Assert.Equal("COST_ESTIMATE_EXCEEDS_LIMIT", overLimit.RejectionCode);

        // 0.09 is inside the per-generation limit but over the workspace ceiling, so this is the
        // denial that must be decided from the read alone, before anything is reserved.
        var overCeiling = await control.CheckPreflightAsync(workspaceId, GatedFeature, 0.09m);
        Assert.False(overCeiling.Allowed);
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", overCeiling.RejectionCode);

        Assert.Equal(0, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());
    }

    // ---------------------------------------------------------------------
    // Exact idempotency: one (workspace, request, feature) key resolves to one
    // pending reservation, a replay never duplicates it, and a terminal
    // reservation is never reopened for a second charge.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Same_request_key_replays_one_pending_reservation_without_a_second_row_or_charge()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        var ledger = CreateLedger(db);

        var first = await ledger.GetOrCreatePendingAsync(
            workspaceId, userId, null, null, "generation:replayed", GatedFeature,
            CancellationToken.None, estimatedProviderCostUsd: 0.10m);
        var replay = await ledger.GetOrCreatePendingAsync(
            workspaceId, userId, null, null, "generation:replayed", GatedFeature,
            CancellationToken.None, estimatedProviderCostUsd: 0.10m);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(UsageTransactionStatus.Pending, replay.Status);
        Assert.Equal("pending", replay.Provider);
        Assert.Equal("pending", replay.Model);
        Assert.Equal(0m, replay.ChargedAmount);
        Assert.False(replay.IsBillable);
        Assert.Equal(1, await db.UsageTransactions.CountAsync());

        // A different request key in the same workspace is a different reservation, so
        // idempotency is exact rather than workspace-wide coalescing.
        var other = await ledger.GetOrCreatePendingAsync(
            workspaceId, userId, null, null, "generation:other-request", GatedFeature,
            CancellationToken.None, estimatedProviderCostUsd: 0.10m);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Terminal_generation_reservation_is_never_reopened_by_a_duplicate_delivery()
    {
        await using var db = await CreateDbAsync();
        var (workspaceId, userId) = await AddOwnerAsync(db);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.ImageGenerate,
            Status = GenerationJobStatus.Failed,
            InputJson = "{}",
            RequestId = "generation:terminal-job",
            CreatedAt = DateTime.UtcNow,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync();

        var ledger = CreateLedger(db);
        var pending = await ledger.GetOrCreatePendingAsync(
            workspaceId, userId, null, null, "generation:terminal-job", GatedFeature,
            CancellationToken.None, generationJobId: job.Id, estimatedProviderCostUsd: 0.10m);
        await ledger.FailAsync(pending, "MOCK_PROVIDER_FAILED");

        var duplicate = await ledger.GetOrCreatePendingAsync(
            workspaceId, userId, null, null, "generation:terminal-job", GatedFeature,
            CancellationToken.None, generationJobId: job.Id, estimatedProviderCostUsd: 0.10m);

        Assert.Equal(pending.Id, duplicate.Id);
        Assert.Equal(UsageTransactionStatus.Failed, duplicate.Status);
        Assert.Equal(0m, duplicate.ChargedAmount);
        Assert.False(duplicate.IsBillable);
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());
    }

    [DisposablePostgresFact]
    public async Task Concurrent_workspace_deliveries_of_one_request_key_commit_exactly_one_reservation_on_postgres()
    {
        var (schema, options) = await CreateDisposableSchemaAsync();
        try
        {
            Guid workspaceId;
            Guid userId;
            await using (var db = new TaslimDbContext(options))
            {
                (workspaceId, userId) = await AddOwnerAsync(db);
            }

            async Task<Guid> ReserveAsync()
            {
                await using var db = new TaslimDbContext(options);
                return (await CreateLedger(db).GetOrCreatePendingAsync(
                    workspaceId, userId, null, null, "generation:contended-key", GatedFeature,
                    CancellationToken.None, estimatedProviderCostUsd: 0.10m)).Id;
            }

            var reserved = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(ReserveAsync)));

            await using var verify = new TaslimDbContext(options);
            var rows = await verify.UsageTransactions.AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal(UsageTransactionStatus.Pending, rows[0].Status);
            Assert.Equal(0m, rows[0].ChargedAmount);
            Assert.False(rows[0].IsBillable);
            Assert.Single(reserved.Distinct());
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    [DisposablePostgresFact]
    public async Task Concurrent_workspace_deliveries_of_distinct_request_keys_reserve_each_once_on_postgres()
    {
        var (schema, options) = await CreateDisposableSchemaAsync();
        try
        {
            Guid workspaceId;
            Guid userId;
            await using (var db = new TaslimDbContext(options))
            {
                (workspaceId, userId) = await AddOwnerAsync(db);
            }

            async Task<Guid> ReserveAsync(int index)
            {
                await using var db = new TaslimDbContext(options);
                return (await CreateLedger(db).GetOrCreatePendingAsync(
                    workspaceId, userId, null, null, $"generation:parallel-{index}", GatedFeature,
                    CancellationToken.None, estimatedProviderCostUsd: 0.10m)).Id;
            }

            var reserved = await Task.WhenAll(Enumerable.Range(0, 5).Select(index => Task.Run(() => ReserveAsync(index))));

            await using var verify = new TaslimDbContext(options);
            Assert.Equal(5, await verify.UsageTransactions.CountAsync(item => item.WorkspaceId == workspaceId));
            Assert.Equal(5, reserved.Distinct().Count());
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    // ---------------------------------------------------------------------
    // Studio route contract: the admission decision is made before a job, a
    // reservation or a provider submission exists, and it is made on the same
    // spend figure the internal guardrails report.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Image_studio_counts_refunded_provider_spend_toward_the_workspace_ceiling()
    {
        using var factory = new FreeTierAdmissionApiFactory(dailyCeilingUsd: 1m);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"admission-known-{Guid.NewGuid():N}@example.com");
        await SeedRefundedAsync(factory, auth, "generation:image-refunded-known", providerCostKnown: true, providerCostUsd: 0.999m);

        var response = await SendWithCsrf(client, auth.PersonalWorkspace.Id, "An image whose workspace already spent its daily ceiling.");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", body.GetProperty("error").GetProperty("code").GetString());
        await AssertNoGenerationSideEffectsAsync(factory, auth.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Image_studio_admits_a_workspace_inside_its_ceiling_and_reserves_exactly_once()
    {
        using var factory = new FreeTierAdmissionApiFactory(dailyCeilingUsd: 1000m);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"admission-admit-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(client, auth.PersonalWorkspace.Id, "An image that must be admitted inside the workspace ceiling.");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id);
        Assert.Equal(GenerationJobTypes.ImageGenerate, job.JobType);

        // Admission reserves the estimate exactly once. It names no provider, so nothing has been
        // submitted anywhere, and it charges nobody.
        var reservation = await db.UsageTransactions.AsNoTracking().SingleAsync(item =>
            item.WorkspaceId == auth.PersonalWorkspace.Id && item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Pending, reservation.Status);
        Assert.Equal("pending", reservation.Provider);
        Assert.Equal("pending", reservation.Model);
        Assert.Equal(0m, reservation.ChargedAmount);
        Assert.False(reservation.IsBillable);

        // Registration grants the free-plan credits, but nothing in admission may debit them.
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync(entry => entry.Type == CreditLedgerEntryType.Debit));
    }

    [Fact]
    public async Task Exhausted_workspace_ceiling_denies_every_member_before_any_job_or_reservation_exists()
    {
        // A zero ceiling is the deterministic form of "this workspace has already spent its plan":
        // any positive request estimate is over it, for any member.
        using var factory = new FreeTierAdmissionApiFactory(dailyCeilingUsd: 0m);
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, $"ceiling-owner-{Guid.NewGuid():N}@example.com");

        var first = await SendWithCsrf(ownerClient, owner.PersonalWorkspace.Id, "An image over an already exhausted workspace ceiling.");
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", firstBody.GetProperty("error").GetProperty("code").GetString());

        // A second member of the same workspace is the abuse path that the per-user active-image
        // guard cannot reach, so the shared workspace ceiling has to stop it too.
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, $"ceiling-member-{Guid.NewGuid():N}@example.com");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = owner.PersonalWorkspace.Id,
                UserId = member.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var second = await SendWithCsrf(memberClient, owner.PersonalWorkspace.Id, "A second member must be denied by the same shared ceiling.");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DAILY_COST_CEILING_EXCEEDED", secondBody.GetProperty("error").GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
            Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
            Assert.Equal(0, await db.CreditLedgerEntries.CountAsync(entry => entry.Type == CreditLedgerEntryType.Debit));
        }
    }

    [Theory]
    [InlineData(typeof(ImageGenerationController))]
    [InlineData(typeof(DocumentGenerationController))]
    [InlineData(typeof(PresentationGenerationController))]
    [InlineData(typeof(ResearchGenerationController))]
    [InlineData(typeof(SocialGenerationController))]
    [InlineData(typeof(VoiceGenerationController))]
    [InlineData(typeof(MusicGenerationController))]
    public void Expensive_studio_entry_points_are_anti_forgery_and_rate_gated_before_admission(Type controllerType)
    {
        var action = controllerType.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpPostAttribute>(inherit: true));
        Assert.NotEmpty(action.GetCustomAttributes<ValidateAntiForgeryTokenAttribute>(inherit: true));
        Assert.NotEmpty(action.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static async Task<TaslimDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static GenerationCostEstimate KnownEstimate(decimal amount) =>
        new(true, amount, UsageCurrencies.Usd, "test-fixture", DateTime.UtcNow, "test", []);

    private static UsageCostControl CreateCostControl(TaslimDbContext db, UsageControlOptions options) =>
        new(db, Options.Create(options), NullLogger<UsageCostControl>.Instance);

    private static UsageLedgerService CreateLedger(TaslimDbContext db) => new(
        db,
        new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
        new SafeUsageChargingService(),
        new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
        NullLogger<UsageLedgerService>.Instance);

    private static UsageTransaction NewTransaction(
        Guid workspaceId,
        Guid userId,
        string requestId,
        UsageTransactionStatus status,
        decimal providerCostUsd,
        bool providerCostKnown) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        RequestId = requestId,
        Feature = GatedFeature,
        Provider = "internal-provider",
        Model = "internal-model",
        Status = status,
        ProviderCostUsd = providerCostUsd,
        ProviderCostKnown = providerCostKnown,
        ChargedAmount = 0m,
        ReversedAmount = 0m,
        Currency = UsageCurrencies.Usd,
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task<(Guid WorkspaceId, Guid UserId)> AddOwnerAsync(TaslimDbContext db)
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Free plan admission",
            Slug = $"free-admission-{workspaceId:N}",
            Type = WorkspaceType.Personal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"free-admission-{userId:N}@example.com",
            NormalizedUserName = $"FREE-ADMISSION-{userId:N}@EXAMPLE.COM",
            Email = $"free-admission-{userId:N}@example.com",
            NormalizedEmail = $"FREE-ADMISSION-{userId:N}@EXAMPLE.COM",
            DisplayName = "Free Admission Tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (workspaceId, userId);
    }

    /// <summary>
    /// Builds the state the append-only ledger leaves behind when a completed generation is
    /// reversed or refunded: the terminal <see cref="UsageTransactionStatus.Refunded"/> status plus
    /// its single adjustment row. Customer charging stays disabled, so the charge is zero.
    /// </summary>
    private static async Task SeedRefundedAsync(
        TaslimDbContext db,
        Guid workspaceId,
        Guid userId,
        string requestId,
        bool providerCostKnown,
        decimal providerCostUsd = 0m)
    {
        var transaction = NewTransaction(workspaceId, userId, requestId, UsageTransactionStatus.Refunded, providerCostUsd, providerCostKnown);
        transaction.IsBillable = false;
        transaction.RefundedAt = DateTime.UtcNow;
        db.UsageTransactions.Add(transaction);
        db.UsageTransactionAdjustments.Add(new UsageTransactionAdjustment
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UsageTransactionId = transaction.Id,
            Type = UsageTransactionAdjustmentType.Reversal,
            AmountUsd = 0m,
            Currency = UsageCurrencies.Usd,
            IdempotencyKey = $"{requestId}:reversal",
            Reason = "Reversal row written by the ledger alongside the refunded status",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRefundedAsync(
        WebApplicationFactory<Program> factory,
        AuthResponse auth,
        string requestId,
        bool providerCostKnown,
        decimal providerCostUsd)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        await SeedRefundedAsync(db, auth.PersonalWorkspace.Id, auth.User.Id, requestId, providerCostKnown, providerCostUsd);
    }

    private static async Task AssertNoGenerationSideEffectsAsync(WebApplicationFactory<Program> factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == workspaceId));
        // A denial may only ever leave the pre-existing history rows in place: no new reservation
        // (the studio keys each reservation on its job) and no credit movement.
        Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(item =>
            item.WorkspaceId == workspaceId && item.RequestId.StartsWith("image-studio:")));
        Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.WorkspaceId == workspaceId));
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync(entry => entry.Type == CreditLedgerEntryType.Debit));
    }

    private static async Task<(string Schema, DbContextOptions<TaslimDbContext> Options)> CreateDisposableSchemaAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"usage_admission_{Guid.NewGuid():N}";
        var adminBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(builder.ConnectionString).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (schema, options);
    }

    private static async Task DropSchemaAsync(string schema)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var adminBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        await using var admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Free Tier Admission Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, Guid workspaceId, string description)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            workspaceId,
            description,
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Image studio with the worker disabled, so an admitted job stays inert and no provider is
    /// ever contacted, and with the workspace ceiling bound from configuration exactly as an
    /// operator would set it.
    /// </summary>
    private sealed class FreeTierAdmissionApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-free-admission-{Guid.NewGuid():N}.db");
        private readonly decimal? dailyCeilingUsd;

        public FreeTierAdmissionApiFactory(decimal? dailyCeilingUsd = null)
        {
            this.dailyCeilingUsd = dailyCeilingUsd;
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
            using var db = new TaslimDbContext(
                new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("GenerationJobs:WorkerEnabled", "False");
            builder.UseSetting("ImageGeneration:Enabled", "true");
            builder.UseSetting("ImageGeneration:ProviderKey", "openai");
            builder.UseSetting("UsageControls:GuardrailsEnabled", "true");
            if (dailyCeilingUsd.HasValue)
            {
                builder.UseSetting(
                    "UsageControls:DailyWorkspaceProviderCostCeilingUsd",
                    dailyCeilingUsd.Value.ToString(CultureInfo.InvariantCulture));
            }

            var path = databasePath;
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={path};Cache=Shared;Default Timeout=30"));
                services.RemoveAll<IImageGenerationProvider>();
                services.AddSingleton<IImageGenerationProvider, DeterministicImageProvider>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            // The ephemeral SQLite file is unique per factory. Like the other studio factories it
            // is left for the process lifetime: a late connection would otherwise recreate an empty
            // database, and clearing the global pool would disrupt the in-memory databases used by
            // tests running in parallel.
        }
    }
}
