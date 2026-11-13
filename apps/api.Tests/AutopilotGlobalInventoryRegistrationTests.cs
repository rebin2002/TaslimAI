using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Xunit;

namespace Taslim.Api.Tests;

// Environment-based configuration is read at WebApplication.CreateBuilder time,
// before WebApplicationFactory's ConfigureAppConfiguration override can be applied.
// Isolate this group from all other xUnit collections to avoid leaking process
// settings into unrelated test hosts.
[CollectionDefinition("AutopilotGlobalInventoryEnvironment", DisableParallelization = true)]
public sealed class AutopilotGlobalInventoryEnvironmentCollection { }

[Collection("AutopilotGlobalInventoryEnvironment")]
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
        var settings = new Dictionary<string, string?>
        {
            ["Autopilot__GlobalInventory__Enabled"] = "true",
            ["Autopilot__GlobalInventory__BridgeTasksUrl"] = "https://bridge.example.test/v1/tasks",
            ["Autopilot__GlobalInventory__ExpectedManusUserId"] = "verified-test-user",
            ["Autopilot__MaxConcurrency"] = "99",
        };
        var original = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var setting in settings)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);

            using var factory = new TaslimApiFactory();
            using var scope = factory.Services.CreateScope();
            Assert.IsType<ManusBridgeUserWideTaskInventoryPageSource>(
                scope.ServiceProvider.GetRequiredService<IAutopilotGlobalTaskInventoryPageSource>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<AutopilotGlobalTaskInventoryReader>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<AutopilotGlobalTaskSlotAllocator>());
            // Registration alone must not enable actual Manus launches.
            var options = scope.ServiceProvider.GetRequiredService<AutopilotOptions>();
            Assert.False(options.Enabled);
            Assert.False(options.AllowNextWaveLaunch);
        }
        finally
        {
            foreach (var setting in original)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }
}
