using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MemoryAuthorizationUnitTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_before_private_memory_operations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var controller = new MemoriesController(db, new WorkspaceAccessService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")],
                    "synthetic")),
            },
        };

        var workspaceId = Guid.NewGuid();
        var memoryId = Guid.NewGuid();
        var createRequest = new CreatePersonalMemoryRequest { Category = "Other", Title = "Private", Content = "Synthetic" };
        var updateRequest = new UpdatePersonalMemoryRequest { Category = "Other", Title = "Private", Content = "Updated" };

        var results = new IActionResult[]
        {
            await controller.List(workspaceId, CancellationToken.None),
            await controller.Create(workspaceId, createRequest, CancellationToken.None),
            await controller.Update(memoryId, updateRequest, CancellationToken.None),
            await controller.Delete(memoryId, CancellationToken.None),
        };

        Assert.All(results, result => Assert.IsType<UnauthorizedResult>(result));
        Assert.Empty(await db.PersonalMemories.ToListAsync());
    }
}
