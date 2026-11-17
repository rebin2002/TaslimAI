using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pins the admin audit export window contract: range bounds supplied with a
/// non-UTC offset are normalized to their exact UTC instants before the audit
/// rows are ordered and truncated to whole UTC days. A bound query value such
/// as "2026-11-16T02:00:00+05:00" arrives as a local-kind DateTime carrying
/// that instant; before the fix NormalizeRange reinterpreted the wall clock as
/// UTC (SpecifyKind), silently shifting the exported window by the offset
/// difference so the echoed range and the audit rows ordered against it were
/// wrong.
/// </summary>
public sealed class AdminAuditExportUtcWindowTests : IClassFixture<TaslimApiFactory>
{
    private static readonly DateTime UtcFrom = new(2026, 11, 15, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UtcTo = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);

    private static readonly System.Reflection.MethodInfo NormalizeRangeMethod =
        typeof(AdminOperationsService)
            .GetMethod("NormalizeRange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private readonly TaslimApiFactory factory;

    public AdminAuditExportUtcWindowTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    private static (DateTime FromUtc, DateTime ToUtc) Normalize(DateTime? fromUtc, DateTime? toUtc)
    {
        var boxed = (ValueTuple<DateTime, DateTime>)NormalizeRangeMethod.Invoke(null, new object?[] { fromUtc, toUtc })!;
        return (boxed.Item1, boxed.Item2);
    }

    // Exactly what MVC model binding produces for an offset-bearing query
    // value: the instant expressed in the host's local calendar (Kind = Local,
    // the same shape DateTime.Parse yields for "2026-11-16T02:00:00+05:00").
    private static DateTime HostLocal(DateTime utcInstant) =>
        DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc).ToLocalTime();

    [Fact]
    public void Offset_bearing_bounds_are_converted_to_their_exact_utc_instants()
    {
        var (fromUtc, toUtc) = Normalize(HostLocal(UtcFrom), HostLocal(UtcTo));

        Assert.Equal(HostLocal(UtcFrom).ToUniversalTime().Date, fromUtc);
        Assert.Equal(HostLocal(UtcTo).ToUniversalTime().Date.AddDays(1), toUtc);
        Assert.Equal(DateTimeKind.Utc, fromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, toUtc.Kind);
    }

    [Fact]
    public void Whole_day_truncation_happens_after_conversion_on_the_utc_calendar()
    {
        // Pick an instant whose UTC day differs from its offset local day:
        // 2026-11-15T21:00:00Z is 2026-11-16T02:00:00+05:00. Truncating the
        // clock value before conversion lands on the wrong UTC day; the
        // export window must follow the UTC calendar of the instant.
        var utcStart = new DateTime(2026, 11, 15, 21, 0, 0, DateTimeKind.Utc);
        var utcEnd = new DateTime(2026, 11, 16, 2, 30, 0, DateTimeKind.Utc);

        // The assertion is expressed against the bound representation so it
        // holds on every host: conversion must precede truncation.
        var (fromUtc, toUtc) = Normalize(HostLocal(utcStart), HostLocal(utcEnd));
        Assert.Equal(HostLocal(utcStart).ToUniversalTime().Date, fromUtc);
        Assert.Equal(HostLocal(utcEnd).ToUniversalTime().Date.AddDays(1), toUtc);
        Assert.InRange(toUtc - fromUtc, TimeSpan.FromDays(1), TimeSpan.FromDays(2));
    }

    [Fact]
    public void Utc_and_unspecified_bounds_keep_their_existing_semantics()
    {
        // Z-suffixed round-trip ("O") bounds keep their exact instants.
        var (passFrom, passTo) = Normalize(UtcFrom, UtcTo);
        Assert.Equal(UtcFrom.Date, passFrom);
        Assert.Equal(UtcTo.Date.AddDays(1), passTo);

        // Date-only bounds arrive without a kind and continue to mean UTC
        // midnight, with the inclusive end day extended to the next UTC midnight.
        var (dayFrom, dayTo) = Normalize(
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), dayFrom);
        Assert.Equal(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), dayTo);
    }

    [Fact]
    public void Rollover_and_cap_are_applied_to_the_converted_bounds()
    {
        var localFrom = HostLocal(UtcFrom);
        var localTo = HostLocal(UtcTo);
        var utcFromDay = localFrom.ToUniversalTime().Date;

        // A one-hour request inside a single UTC day: the end must roll to the
        // next UTC midnight after conversion.
        var (sameFrom, sameTo) = Normalize(
            HostLocal(new DateTime(2026, 11, 15, 1, 0, 0, DateTimeKind.Utc)),
            HostLocal(new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(HostLocal(new DateTime(2026, 11, 15, 1, 0, 0, DateTimeKind.Utc)).ToUniversalTime().Date, sameFrom);
        Assert.Equal(sameFrom.AddDays(1), sameTo);

        // An oversized window keeps the converted end and pulls the start
        // forward to the cap; the old code shifted both bounds by the offset.
        var (longFrom, longTo) = Normalize(localFrom.AddDays(-730), localTo);
        Assert.Equal(longTo.AddDays(-366), longFrom);
        Assert.Equal(utcFromDay.AddDays(2), longTo);
    }

    [Fact]
    public async Task Dashboard_echoes_the_utc_normalized_window_and_exports_timestamp_ordered_audit_rows()
    {
        var marker = $"utc-window-{Guid.NewGuid():N}";
        var actorUserId = Guid.NewGuid();
        var anchor = UtcTo;
        var insideOlder = BuildAudit(marker, anchor.AddDays(-1), actorUserId);
        var insideNewer = BuildAudit(marker, anchor.AddHours(-5), actorUserId);
        var outsideFuture = BuildAudit(marker, anchor.AddDays(3), actorUserId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Users.Add(new Taslim.Api.Domain.ApplicationUser
            {
                Id = actorUserId,
                UserName = $"audit-utc-window-{actorUserId:N}@example.com",
                NormalizedUserName = $"AUDIT-UTC-WINDOW-{actorUserId:N}@EXAMPLE.COM",
                Email = $"audit-utc-window-{actorUserId:N}@example.com",
                NormalizedEmail = $"AUDIT-UTC-WINDOW-{actorUserId:N}@EXAMPLE.COM",
                DisplayName = "Audit Window Test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.AdminOperationAuditEvents.AddRange(insideOlder, insideNewer, outsideFuture);
            await db.SaveChangesAsync();
        }

        var service = factory.Services.GetRequiredService<IAdminOperationsService>();
        var filter = new AdminOperationsFilter(HostLocal(UtcFrom), HostLocal(UtcTo));

        var dashboard = await service.GetDashboardAsync(filter);

        // The exported window must be the UTC-normalized bounds: before the
        // fix the offset-bearing clock value was reinterpreted as UTC,
        // drifting the echoed range and ordering the exported audit rows
        // against boundaries that no longer matched the requested instants.
        Assert.Equal(HostLocal(UtcFrom).ToUniversalTime().Date, dashboard.Range.FromUtc);
        Assert.Equal(HostLocal(UtcTo).ToUniversalTime().Date.AddDays(1), dashboard.Range.ToUtc);

        var createdAt = dashboard.RecentAdminActions
            .Select(item => DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc))
            .ToArray();
        Assert.True(createdAt.OrderByDescending(value => value).SequenceEqual(createdAt),
            "Admin audit export rows must be ordered newest-first by their UTC timestamp.");

        var exportedIds = dashboard.RecentAdminActions.Select(item => item.Id).ToArray();
        Assert.Contains(outsideFuture.Id, exportedIds);
        var futureIndex = Array.IndexOf(exportedIds, outsideFuture.Id);
        var futureCreatedAt = createdAt[futureIndex];
        foreach (var earlier in createdAt.Take(futureIndex))
        {
            Assert.True(earlier >= futureCreatedAt,
                "Rows ahead of the newest seeded row must carry newer UTC timestamps.");
        }
        Assert.Contains(exportedIds, id => id == insideNewer.Id || id == insideOlder.Id);

        using var cleanup = factory.Services.CreateScope();
        var cleanupDb = cleanup.ServiceProvider.GetRequiredService<TaslimDbContext>();
        await cleanupDb.AdminOperationAuditEvents
            .Where(item => item.Reason != null && item.Reason.StartsWith(marker))
            .ExecuteDeleteAsync();
        var user = await cleanupDb.Users.FindAsync(actorUserId);
        if (user is not null)
        {
            cleanupDb.Users.Remove(user);
            await cleanupDb.SaveChangesAsync();
        }
    }

    private static AdminOperationAuditEvent BuildAudit(string marker, DateTime createdAt, Guid actorUserId) => new()
    {
        Id = Guid.NewGuid(),
        ActorUserId = actorUserId,
        Action = AdminOperationActions.RecoverExpiredGenerationJob,
        TargetType = "generation_job",
        TargetId = Guid.NewGuid(),
        Outcome = AdminOperationOutcomes.Succeeded,
        Reason = marker,
        CreatedAt = createdAt,
    };
}
