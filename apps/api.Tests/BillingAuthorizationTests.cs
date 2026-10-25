using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Billing;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class BillingAuthorizationTests
{
    [Fact]
    public async Task Billing_account_is_private_to_workspace_owners_and_admins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Billing authorization workspace",
            Slug = $"billing-authorization-{Guid.NewGuid():N}",
            Type = WorkspaceType.Business,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        db.Workspaces.Add(workspace);
        db.Users.AddRange(NewUser(ownerId), NewUser(adminId), NewUser(memberId));
        db.WorkspaceMembers.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = ownerId, Role = WorkspaceRole.Owner },
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = adminId, Role = WorkspaceRole.Admin },
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = memberId, Role = WorkspaceRole.Member });
        await db.SaveChangesAsync();

        var billing = new RecordingBillingAccountService();
        var memberResult = await CreateController(db, billing, memberId).Get(workspace.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(memberResult);
        Assert.Equal(0, billing.CallCount);

        var ownerResult = await CreateController(db, billing, ownerId).Get(workspace.Id, CancellationToken.None);
        var adminResult = await CreateController(db, billing, adminId).Get(workspace.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(ownerResult);
        Assert.IsType<OkObjectResult>(adminResult);
        Assert.Equal(2, billing.CallCount);
    }

    private static BillingController CreateController(TaslimDbContext db, IBillingAccountService billing, Guid userId)
    {
        var controller = new BillingController(new WorkspaceAccessService(db), billing)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        authenticationType: "synthetic-test")),
                },
            },
        };
        return controller;
    }

    private static ApplicationUser NewUser(Guid id) => new()
    {
        Id = id,
        UserName = $"billing-{id:N}@example.com",
        NormalizedUserName = $"BILLING-{id:N}@EXAMPLE.COM",
        Email = $"billing-{id:N}@example.com",
        NormalizedEmail = $"BILLING-{id:N}@EXAMPLE.COM",
        DisplayName = "Billing test user",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class RecordingBillingAccountService : IBillingAccountService
    {
        public int CallCount { get; private set; }

        public Task<BillingAccountDto> GetAccountAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<BillingAccountDto>(null!);
        }
    }
}
