using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Keeps Bridge inventory admission behind explicit deployment configuration.
/// No test enables external Manus launches or reads production secrets.
/// </summary>
public sealed class AutopilotGlobalInventoryRegistrationTests
{
    [Fact]
    public void Inventory_allocator_is_not_registered_by_default()
    {
        using var factory = new TaslimApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IAutopilotGlobalTaskInventoryPageSource>());
        Assert.Null(scope.ServiceProvider.GetService<AutopilotGlobalTaskInventoryReader>());
        Assert.Null(scope.ServiceProvider.GetService<AutopilotGlobalTaskSlotAllocator>());
    }

    [Fact]
    public void Explicit_userwide_inventory_configuration_registers_complete_admission_stack()
    {
        using var baseFactory = new TaslimApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Autopilot:GlobalInventory:Enabled"] = "true",
                    ["Autopilot:GlobalInventory:BridgeTasksUrl"] = "https://bridge.example.test/v1/tasks",
                    ["Autopilot:GlobalInventory:ExpectedManusUserId"] = "verified-test-user",
                    ["Autopilot:MaxConcurrency"] = "99",
                })));

        using var scope = factory.Services.CreateScope();
        Assert.IsType<ManusBridgeUserWideTaskInventoryPageSource>(
            scope.ServiceProvider.GetRequiredService<IAutopilotGlobalTaskInventoryPageSource>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AutopilotGlobalTaskInventoryReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AutopilotGlobalTaskSlotAllocator>());
        // Configuration alone never enables the controller or external launches.
        var options = scope.ServiceProvider.GetRequiredService<AutopilotOptions>();
        Assert.False(options.Enabled);
        Assert.False(options.AllowNextWaveLaunch);
    }
}
