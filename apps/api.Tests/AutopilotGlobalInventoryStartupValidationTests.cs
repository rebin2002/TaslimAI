using Xunit;

namespace Taslim.Api.Tests;

[Collection("AutopilotGlobalInventoryEnvironment")]
public sealed class AutopilotGlobalInventoryStartupValidationTests
{
    private const string StartupValidationMessage =
        "Autopilot global inventory requires a validated HTTPS BridgeTasksUrl and verified ExpectedManusUserId.";

    [Fact]
    public void Invalid_expected_manus_user_id_prevents_startup()
    {
        AssertStartupRejected(
            "https://bridge.example.test/v1/tasks",
            "verified test user");
    }

    [Fact]
    public void Invalid_bridge_tasks_url_prevents_startup()
    {
        AssertStartupRejected(
            "http://bridge.example.test/v1/tasks",
            "verified-test-user");
    }

    private static void AssertStartupRejected(string bridgeTasksUrl, string expectedManusUserId)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Autopilot__GlobalInventory__Enabled"] = "true",
            ["Autopilot__GlobalInventory__BridgeTasksUrl"] = bridgeTasksUrl,
            ["Autopilot__GlobalInventory__ExpectedManusUserId"] = expectedManusUserId,
        };
        var original = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var setting in settings)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);

            using var factory = new TaslimApiFactory();
            var startupException = Record.Exception(() => _ = factory.Services);

            Assert.NotNull(startupException);
            Assert.Contains(StartupValidationMessage, startupException.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var setting in original)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }
}
