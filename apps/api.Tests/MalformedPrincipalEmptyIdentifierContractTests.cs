using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Activity;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Taslim.Api.Notifications;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Review evidence for the consolidated server-side malformed-principal gates.
/// A parseable identifier is not automatically a usable one: every Guid.Empty textual
/// form is the default value of an unset user identifier, so a principal gate that accepts
/// it stays open until some downstream membership check happens to close it. Files and movie
/// dialogue already reject Guid.Empty; these cases pin the same fail-closed contract for all
/// eight reviewed areas and prove that a well-formed identifier is never locked out by it.
/// </summary>
public sealed class MalformedPrincipalEmptyIdentifierContractTests
{
    private const string ZeroDashForm = "00000000-0000-0000-0000-000000000000";
    private const string ZeroCompactForm = "00000000000000000000000000000000";
    private const string ZeroBraceForm = "{00000000-0000-0000-0000-000000000000}";
    private const string ZeroHexForm = "{0x0,0x0,0x0,{0x0,0x0,0x0,0x0,0x0,0x0,0x0,0x0}}";

    public static TheoryData<string?> UnusableIdentifiers => new()
    {
        null,
        string.Empty,
        "   ",
        "not-a-guid",
        ZeroDashForm,
        ZeroCompactForm,
        ZeroBraceForm,
        ZeroHexForm,
    };

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Activity_fails_closed_before_the_activity_center_is_reached(string? identifier)
    {
        var service = new NeverCalledActivityCenterService();
        var controller = new ActivityController(service) { ControllerContext = Context(identifier) };
        var workspaceId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.List(workspaceId),
            () => controller.UnreadCount(workspaceId, default),
            () => controller.MarkRead(Guid.NewGuid(), new ActivityReadRequest(workspaceId), default),
            () => controller.MarkAllRead(new ActivityReadAllRequest(workspaceId), default),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Files_fails_closed_before_the_file_store_is_reached(string? identifier)
    {
        var controller = new FilesController(null!, null!, null!, null!) { ControllerContext = Context(identifier) };
        var workspaceId = Guid.NewGuid();
        var file = new FormFile(new MemoryStream([0x01]), 0, 1, "file", "notes.txt");
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Upload(workspaceId, file, null, null, default),
            () => controller.List(workspaceId, null, null, cancellationToken: default),
            () => controller.Get(Guid.NewGuid(), default),
            () => controller.Delete(Guid.NewGuid(), default),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Private_memory_fails_closed_before_anything_is_read_or_written(string? identifier)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new MemoriesController(db, new WorkspaceAccessService(db)) { ControllerContext = Context(identifier) };
        var workspaceId = Guid.NewGuid();
        var results = new Func<Task<IActionResult>>[]
        {
            () => controller.List(workspaceId, CancellationToken.None),
            () => controller.Create(workspaceId, new CreatePersonalMemoryRequest { Category = "Other", Title = "Private", Content = "Synthetic" }, CancellationToken.None),
            () => controller.Update(Guid.NewGuid(), new UpdatePersonalMemoryRequest { Category = "Other", Title = "Private", Content = "Updated" }, CancellationToken.None),
            () => controller.Delete(Guid.NewGuid(), CancellationToken.None),
        };
        foreach (var result in results)
            Assert.IsType<UnauthorizedResult>(await result());
        Assert.Empty(await db.PersonalMemories.ToListAsync());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Movie_dialogue_fails_closed_before_the_dialogue_service_is_reached(string? identifier)
    {
        var controller = new MovieDialogueController(null!) { ControllerContext = Context(identifier) };
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Get(Guid.NewGuid(), CancellationToken.None),
            () => controller.AddLine(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.QueueTake(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.Approve(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.Select(Guid.NewGuid(), CancellationToken.None),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Movie_final_assembly_fails_closed_before_the_assembly_service_is_reached(string? identifier)
    {
        var controller = new MovieFinalAssemblyController(null!, null!, null!, null!) { ControllerContext = Context(identifier) };
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Queue(Guid.NewGuid(), new MovieFinalAssemblyRequest(), CancellationToken.None),
            () => controller.Get(Guid.NewGuid(), CancellationToken.None),
            () => controller.Download(Guid.NewGuid(), CancellationToken.None),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Movie_sound_fails_closed_before_the_sound_service_is_reached(string? identifier)
    {
        var controller = new MovieSoundController(null!) { ControllerContext = Context(identifier) };
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.GetLibrary(Guid.NewGuid(), CancellationToken.None),
            () => controller.AddLibraryReference(Guid.NewGuid(), new MovieSoundLibraryReferenceRequest { AssetId = Guid.NewGuid() }, CancellationToken.None),
            () => controller.GetSceneTracks(Guid.NewGuid(), CancellationToken.None),
            () => controller.CreateSceneTrack(Guid.NewGuid(), new MovieSoundTrackRequest(), CancellationToken.None),
            () => controller.GetShotTracks(Guid.NewGuid(), CancellationToken.None),
            () => controller.CreateShotTrack(Guid.NewGuid(), new MovieSoundTrackRequest(), CancellationToken.None),
            () => controller.GetTrack(Guid.NewGuid(), CancellationToken.None),
            () => controller.Review(Guid.NewGuid(), new MovieSoundReviewRequest(), CancellationToken.None),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Notifications_fails_closed_before_the_notification_service_is_reached(string? identifier)
    {
        var controller = new NotificationsController(null!) { ControllerContext = Context(identifier) };
        var workspaceId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.List(workspaceId),
            () => controller.UnreadCount(workspaceId, default),
            () => controller.MarkRead(Guid.NewGuid(), new NotificationReadRequest(workspaceId), default),
            () => controller.MarkAllRead(new NotificationReadAllRequest(workspaceId), default),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Theory]
    [MemberData(nameof(UnusableIdentifiers))]
    public async Task Upscaling_fails_closed_before_an_expensive_job_is_queued(string? identifier)
    {
        var controller = new UpscalingController(null!) { ControllerContext = Context(identifier) };
        var workspaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Create(new CreateUpscalingJobRequest { WorkspaceId = workspaceId, SourceAssetId = Guid.NewGuid(), TargetResolution = "1080p" }, default),
            () => controller.Get(jobId, default),
            () => controller.List(workspaceId, cancellationToken: default),
            () => controller.Retry(jobId, default),
            () => controller.Cancel(jobId, default),
            () => controller.ReviewQuality(jobId, new ReviewUpscalingQualityRequest { Approved = true }, default),
        };
        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Fact]
    public async Task A_wellformed_identifier_still_reaches_workspace_membership()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new MemoriesController(db, new WorkspaceAccessService(db))
        {
            ControllerContext = Context(Guid.NewGuid().ToString("D")),
        };
        // The hardened gate must not lock a legitimate session out of its own request:
        // the principal gate lets it through and workspace membership does the refusing.
        Assert.IsType<ForbidResult>(await controller.List(Guid.NewGuid(), CancellationToken.None));
    }

    private static ControllerContext Context(string? identifier)
    {
        IEnumerable<Claim> claims = identifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) };
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Synthetic")),
            },
        };
    }

    private sealed class NeverCalledActivityCenterService : IActivityCenterService
    {
        public Task<ActivityListDto?> ListAsync(Guid userId, ActivityFilter filter, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Activity service must not be reached.");
        public Task<int?> GetUnreadCountAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Activity service must not be reached.");
        public Task<bool> MarkReadAsync(Guid userId, Guid jobId, Guid workspaceId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Activity service must not be reached.");
        public Task<bool> MarkAllReadAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Activity service must not be reached.");
    }
}
