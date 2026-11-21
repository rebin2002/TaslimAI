using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic safety contract for the database-backed Autopilot lease
/// (<see cref="EfAutopilotLockService"/>). Every wave, event, and release
/// transition is serialized behind this lease, so a regression here is what
/// allows duplicate integrations, releases, or waves.
///
/// The cases are written as invariants of the lease protocol rather than of one
/// clock implementation: expiry is always forced by moving the persisted
/// <c>ExpiresAt</c> into the past, never by sleeping; no case asserts an exact
/// fencing-token value, only the ordering the protocol guarantees; and the
/// persisted state is always re-read through a fresh DbContext, because the
/// lease is written with <c>ExecuteUpdateAsync</c>, which bypasses the change
/// tracker of the scope that issued it. Nothing here reaches a provider, a
/// wallet, or a webhook, and no test enables a refill path or changes a
/// concurrency quota.
/// </summary>
public sealed class AutopilotLeaseSafetyTests : IDisposable
{
    private const string HolderA = "holder-a";
    private const string HolderB = "holder-b";
    private const string HolderC = "holder-c";

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public AutopilotLeaseSafetyTests()
    {
        connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    // ------------------------------------------------------------------
    // Grant and exclusivity
    // ------------------------------------------------------------------

    [Fact]
    public async Task First_acquire_grants_an_exclusive_lease_with_a_fencing_token()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-grant");

        var handle = await locks.AcquireAsync(resource, HolderA);

        Assert.True(handle.IsAcquired);
        Assert.Equal(resource, handle.ResourceKey);
        Assert.Equal(HolderA, handle.HeldBy);
        Assert.NotEmpty(handle.OwnerToken);
        Assert.True(handle.FencingToken >= 1, "a granted lease must carry a usable fencing token");
        Assert.NotNull(handle.ExpiresAt);
        Assert.True(handle.ExpiresAt!.Value > DateTime.UtcNow.AddSeconds(55), "the default lease must be live");

        var row = await ReadLeaseAsync(resource);
        Assert.Equal(resource, row.ResourceKey);
        Assert.Equal(handle.OwnerToken, row.OwnerToken);
        Assert.Equal(handle.FencingToken, row.FencingToken);
        Assert.Equal(handle.ExpiresAt!.Value, row.ExpiresAt);
        Assert.Equal(HolderA, row.HeldBy);
    }

    [Fact]
    public async Task A_live_lease_blocks_a_second_holder_without_disturbing_the_owner()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-live");

        var owner = await locks.AcquireAsync(resource, HolderA);
        Assert.True(owner.IsAcquired);

        var contender = await locks.AcquireAsync(resource, HolderB);

        Assert.False(contender.IsAcquired);
        Assert.False(contender.Acquired);
        Assert.Equal(string.Empty, contender.OwnerToken);
        Assert.Equal(0, contender.FencingToken);
        Assert.Equal(HolderA, contender.HeldBy);
        Assert.Equal(owner.ExpiresAt, contender.ExpiresAt);

        var row = await ReadLeaseAsync(resource);
        Assert.Equal(owner.OwnerToken, row.OwnerToken);
        Assert.Equal(owner.FencingToken, row.FencingToken);
        Assert.Equal(HolderA, row.HeldBy);
    }

    [Fact]
    public async Task Lease_state_is_persisted_so_a_second_service_instance_sees_the_holder()
    {
        var resource = EfAutopilotLockService.WaveResource("lease-persisted");
        AutopilotLockHandle owner;
        using (var firstContext = CreateContext())
        {
            owner = await new EfAutopilotLockService(firstContext, LeaseOptions(15)).AcquireAsync(resource, HolderA);
            Assert.True(owner.IsAcquired);
        }

        // A different DbContext (a different request or scope in production) must
        // contend against the same row rather than a per-instance cache.
        using var secondContext = CreateContext();
        var contender = await new EfAutopilotLockService(secondContext, LeaseOptions(15)).AcquireAsync(resource, HolderB);

        Assert.False(contender.IsAcquired);
        Assert.Equal(HolderA, contender.HeldBy);
    }

    // ------------------------------------------------------------------
    // Expiry, takeover and fencing
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_expired_lease_is_stolen_with_a_strictly_higher_fencing_token()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-expired");

        var stale = await locks.AcquireAsync(resource, HolderA);
        Assert.True(stale.IsAcquired);
        await ExpireAsync(resource);

        var successor = await locks.AcquireAsync(resource, HolderC);

        Assert.True(successor.IsAcquired);
        Assert.NotEqual(stale.OwnerToken, successor.OwnerToken);
        Assert.True(successor.FencingToken > stale.FencingToken, "a takeover must always raise the fencing token");

        var row = await ReadLeaseAsync(resource);
        Assert.Equal(successor.OwnerToken, row.OwnerToken);
        Assert.Equal(successor.FencingToken, row.FencingToken);
        Assert.Equal(HolderC, row.HeldBy);
    }

    [Fact]
    public async Task The_same_holder_reacquiring_after_expiry_is_fenced_out_of_its_old_incarnation()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-same-holder");

        var first = await locks.AcquireAsync(resource, HolderA);
        await ExpireAsync(resource);
        var second = await locks.AcquireAsync(resource, HolderA);

        Assert.True(second.IsAcquired);
        Assert.Equal(HolderA, second.HeldBy);
        Assert.NotEqual(first.OwnerToken, second.OwnerToken);
        Assert.True(second.FencingToken > first.FencingToken);
        Assert.Equal(second.FencingToken, (await ReadLeaseAsync(resource)).FencingToken);
    }

    [Fact]
    public async Task Fencing_tokens_increase_monotonically_across_repeated_takeovers()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-monotonic");

        var current = await locks.AcquireAsync(resource, HolderA);
        Assert.True(current.IsAcquired);
        var issuedTokens = new List<long> { current.FencingToken };
        var issuedOwners = new List<string> { current.OwnerToken };

        for (var round = 0; round < 4; round++)
        {
            await ExpireAsync(resource);
            var next = await locks.AcquireAsync(resource, $"holder-{round}");

            Assert.True(next.IsAcquired, $"takeover round {round} must be able to acquire the expired lease");
            Assert.True(next.FencingToken > issuedTokens[^1], $"takeover round {round} must raise the fencing token");
            Assert.DoesNotContain(next.FencingToken, issuedTokens);
            Assert.DoesNotContain(next.OwnerToken, issuedOwners);
            issuedTokens.Add(next.FencingToken);
            issuedOwners.Add(next.OwnerToken);
            current = next;
        }

        Assert.Equal(current.FencingToken, (await ReadLeaseAsync(resource)).FencingToken);
    }

    [Fact]
    public async Task A_stale_holder_cannot_release_the_lease_it_no_longer_owns()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-stale-release");

        var stale = await locks.AcquireAsync(resource, HolderA);
        await ExpireAsync(resource);
        var current = await locks.AcquireAsync(resource, HolderB);
        Assert.True(current.IsAcquired);

        Assert.False(await locks.ReleaseAsync(stale));

        var row = await ReadLeaseAsync(resource);
        Assert.Equal(current.OwnerToken, row.OwnerToken);
        Assert.Equal(current.FencingToken, row.FencingToken);
        Assert.Equal(HolderB, row.HeldBy);
        Assert.True(row.ExpiresAt > DateTime.UtcNow.AddSeconds(55), "the live lease must survive a stale release");

        Assert.True(await locks.ReleaseAsync(current));
    }

    // ------------------------------------------------------------------
    // Release
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_live_owner_can_release_and_the_lease_is_reacquirable_in_the_same_instant()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-release");

        var owner = await locks.AcquireAsync(resource, HolderA);
        var tokenBeforeRelease = owner.FencingToken;

        Assert.True(await locks.ReleaseAsync(owner));

        var released = await ReadLeaseAsync(resource);
        Assert.Equal(string.Empty, released.OwnerToken);
        Assert.Equal(tokenBeforeRelease, released.FencingToken);
        Assert.True(released.ExpiresAt <= DateTime.UtcNow, "a released lease must not stay live");

        // No clock advance is required: release makes the resource immediately available.
        var successor = await locks.AcquireAsync(resource, HolderB);
        Assert.True(successor.IsAcquired);
        Assert.True(successor.FencingToken > tokenBeforeRelease, "release must not rewind the fencing sequence");
        Assert.Equal(successor.OwnerToken, (await ReadLeaseAsync(resource)).OwnerToken);
    }

    [Fact]
    public async Task Releasing_a_handle_that_never_owned_a_lease_changes_nothing()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-ghost");

        var owner = await locks.AcquireAsync(resource, HolderA);
        var before = await ReadLeaseAsync(resource);

        Assert.False(await locks.ReleaseAsync(AutopilotLockHandle.NotAcquired(resource, HolderA, owner.ExpiresAt)));

        var after = await ReadLeaseAsync(resource);
        Assert.Equal(before.OwnerToken, after.OwnerToken);
        Assert.Equal(before.FencingToken, after.FencingToken);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.HeldBy, after.HeldBy);
    }

    [Fact]
    public async Task Release_cannot_double_free_a_lease_or_leave_the_resource_claimed()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var resource = EfAutopilotLockService.WaveResource("lease-double-release");

        var owner = await locks.AcquireAsync(resource, HolderA);
        Assert.True(await locks.ReleaseAsync(owner));

        // The second release must not be reported as freeing a live lease, and it
        // must not keep the resource claimed behind the next holder's back.
        Assert.False(await locks.ReleaseAsync(owner));

        var contender = await locks.AcquireAsync(resource, HolderB);
        Assert.True(contender.IsAcquired);
        Assert.Equal(contender.OwnerToken, (await ReadLeaseAsync(resource)).OwnerToken);
    }

    // ------------------------------------------------------------------
    // Lease identity and bounds
    // ------------------------------------------------------------------

    [Fact]
    public async Task Wave_event_and_release_namespaces_never_share_one_lease()
    {
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        const string identifier = "shared-identifier";

        var waveResource = EfAutopilotLockService.WaveResource(identifier);
        var eventResource = EfAutopilotLockService.EventResource(identifier);
        var releaseResource = EfAutopilotLockService.ReleaseResource(identifier);

        Assert.StartsWith("autopilot:wave:", waveResource);
        Assert.StartsWith("autopilot:event:", eventResource);
        Assert.StartsWith("autopilot:release:", releaseResource);
        Assert.NotEqual(waveResource, eventResource);
        Assert.NotEqual(waveResource, releaseResource);
        Assert.NotEqual(eventResource, releaseResource);

        var wave = await locks.AcquireAsync(waveResource, HolderA);
        var completionEvent = await locks.AcquireAsync(eventResource, HolderB);
        var release = await locks.AcquireAsync(releaseResource, HolderC);

        Assert.True(wave.IsAcquired);
        Assert.True(completionEvent.IsAcquired);
        Assert.True(release.IsAcquired);
        using (var counter = CreateContext())
        {
            Assert.Equal(3, await counter.AutopilotLocks.CountAsync());
        }

        // Within one namespace the same identifier still contends.
        var contended = await locks.AcquireAsync(waveResource, HolderB);
        Assert.False(contended.IsAcquired);
        Assert.Equal(HolderA, contended.HeldBy);
    }

    [Fact]
    public async Task Releasing_one_resource_leaves_every_other_lease_locked()
    {
        // A release is addressed by resource key, owner token, and fencing token,
        // so freeing one wave can never free or shorten a lease another transition
        // still relies on.
        using var db = CreateContext();
        var locks = new EfAutopilotLockService(db, LeaseOptions(15));
        var waveOne = EfAutopilotLockService.WaveResource("isolation-one");
        var waveTwo = EfAutopilotLockService.WaveResource("isolation-two");
        var completionEvent = EfAutopilotLockService.EventResource("isolation-one");

        var first = await locks.AcquireAsync(waveOne, HolderA);
        var second = await locks.AcquireAsync(waveTwo, HolderB);
        var third = await locks.AcquireAsync(completionEvent, HolderC);
        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.True(third.IsAcquired);

        Assert.True(await locks.ReleaseAsync(first));

        var stillHeld = await locks.AcquireAsync(waveTwo, HolderA);
        Assert.False(stillHeld.IsAcquired);
        Assert.Equal(HolderB, stillHeld.HeldBy);
        var eventStillHeld = await locks.AcquireAsync(completionEvent, HolderA);
        Assert.False(eventStillHeld.IsAcquired);
        Assert.Equal(HolderC, eventStillHeld.HeldBy);

        // The released resource is available again and is fenced above its own history.
        var reacquired = await locks.AcquireAsync(waveOne, HolderC);
        Assert.True(reacquired.IsAcquired);
        Assert.True(reacquired.FencingToken > first.FencingToken);
        Assert.Equal(reacquired.OwnerToken, (await ReadLeaseAsync(waveOne)).OwnerToken);
        Assert.Equal(second.OwnerToken, (await ReadLeaseAsync(waveTwo)).OwnerToken);
        Assert.Equal(third.OwnerToken, (await ReadLeaseAsync(completionEvent)).OwnerToken);
    }

    [Fact]
    public async Task A_misconfigured_lease_never_shortens_below_one_minute()
    {
        // Deliberately unnormalized: the service must floor the lease itself, so a
        // zero or negative setting can never produce a lease that expires on
        // arrival and lets two writers into the same wave.
        foreach (var configured in new[] { 0, -1 })
        {
            using var db = CreateContext();
            var locks = new EfAutopilotLockService(db, LeaseOptions(configured));
            var resource = EfAutopilotLockService.WaveResource($"lease-floor-{configured}");

            var handle = await locks.AcquireAsync(resource, HolderA);

            Assert.True(handle.IsAcquired);
            Assert.NotNull(handle.ExpiresAt);
            Assert.True(
                handle.ExpiresAt!.Value > DateTime.UtcNow.AddSeconds(55),
                $"LockLeaseMinutes={configured} must still hold the lease for at least a minute");
            Assert.Equal(handle.ExpiresAt!.Value, (await ReadLeaseAsync(resource)).ExpiresAt);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Forces the lease to be expired by moving the persisted deadline into the
    /// past, which keeps every case clock-independent and free of sleeps.
    /// </summary>
    private async Task ExpireAsync(string resourceKey)
    {
        using var db = CreateContext();
        var row = await db.AutopilotLocks.SingleAsync(item => item.ResourceKey == resourceKey);
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Reads the lease exactly as the next scope would see it. The lease is written
    /// with <c>ExecuteUpdateAsync</c>, which does not refresh the change tracker of
    /// the scope that issued the previous write, so verification never reads back
    /// through the writing context.
    /// </summary>
    private async Task<AutopilotLock> ReadLeaseAsync(string resourceKey)
    {
        using var db = CreateContext();
        return await db.AutopilotLocks.AsNoTracking().SingleAsync(item => item.ResourceKey == resourceKey);
    }

    private TaslimDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static AutopilotOptions LeaseOptions(int leaseMinutes) => new()
    {
        Enabled = true,
        DryRun = true,
        SimulationMode = true,
        RequireSignedEvents = false,
        LockLeaseMinutes = leaseMinutes,
    };

    public void Dispose()
    {
        connection.Close();
        connection.Dispose();
    }
}
