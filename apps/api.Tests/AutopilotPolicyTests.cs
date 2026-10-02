using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Taslim.Api.Autopilot;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic policy tests for the Autopilot safety and state-machine
/// foundation. These run without a database and pin the boundaries between
/// automatic repair and human decisions.
/// </summary>
public sealed class AutopilotPolicyTests
{
    [Fact]
    public void Feature_flag_and_live_actions_are_off_by_default()
    {
        var options = new AutopilotOptions();
        options.Normalize();
        Assert.False(options.Enabled);
        Assert.True(options.DryRun);
        Assert.True(options.SimulationMode);
        Assert.False(options.ChargingEnabled);
        Assert.False(options.PaidProvidersEnabled);
        Assert.False(options.AllowAutomaticIntegrationMerge);
        Assert.False(options.AllowAutomaticRelease);
        Assert.Equal(20, options.MaxConcurrency);
        Assert.Equal(60, options.WatchdogIntervalMinutes);
    }

    [Fact]
    public void Max_concurrency_and_watchdog_interval_are_bounded()
    {
        var options = new AutopilotOptions { MaxConcurrency = 500, WatchdogIntervalMinutes = 1, MaxTaskAttempts = 99 };
        options.Normalize();
        Assert.Equal(20, options.MaxConcurrency);
        Assert.Equal(5, options.WatchdogIntervalMinutes);
        Assert.Equal(10, options.MaxTaskAttempts);
    }

    [Theory]
    [InlineData(AutopilotRunStates.Planned, AutopilotRunStates.AwaitingEvents, true)]
    [InlineData(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.InProgress, true)]
    [InlineData(AutopilotRunStates.TasksComplete, AutopilotRunStates.IntegrationGate, true)]
    [InlineData(AutopilotRunStates.IntegrationGate, AutopilotRunStates.ReleaseGate, true)]
    [InlineData(AutopilotRunStates.ReleaseGate, AutopilotRunStates.ReleaseEligible, true)]
    [InlineData(AutopilotRunStates.ReleaseEligible, AutopilotRunStates.HandoffPendingHuman, true)]
    [InlineData(AutopilotRunStates.SmokePassed, AutopilotRunStates.NextWaveEligible, true)]
    [InlineData(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.SmokePassed, false)]
    [InlineData(AutopilotRunStates.TasksComplete, AutopilotRunStates.ReleaseEligible, false)]
    [InlineData(AutopilotRunStates.Cancelled, AutopilotRunStates.InProgress, false)]
    public void Run_state_machine_only_allows_declared_transitions(string from, string to, bool expected) =>
        Assert.Equal(expected, AutopilotStateMachine.CanTransitionRun(from, to));

    [Theory]
    [InlineData(AutopilotTaskStates.Pending, AutopilotTaskStates.Running, true)]
    [InlineData(AutopilotTaskStates.Running, AutopilotTaskStates.RetryScheduled, true)]
    [InlineData(AutopilotTaskStates.RetryScheduled, AutopilotTaskStates.Succeeded, true)]
    [InlineData(AutopilotTaskStates.Succeeded, AutopilotTaskStates.Running, false)]
    [InlineData(AutopilotTaskStates.TerminalFailed, AutopilotTaskStates.Running, false)]
    public void Task_state_machine_only_allows_declared_transitions(string from, string to, bool expected) =>
        Assert.Equal(expected, AutopilotStateMachine.CanTransitionTask(from, to));

    [Fact]
    public void Only_ordinary_and_infrastructure_failures_may_retry_automatically()
    {
        Assert.True(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Ordinary, 0, 3));
        Assert.True(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Infrastructure, 1, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Security, 0, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Migration, 0, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.BrowserE2E, 0, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Accounting, 0, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Integration, 0, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Unknown, 0, 3));
    }

    [Fact]
    public void Retry_attempts_are_bounded_and_never_infinite()
    {
        Assert.True(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Ordinary, 2, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Ordinary, 3, 3));
        Assert.False(AutopilotRetryPolicy.CanRetry(AutopilotFailureClasses.Ordinary, 0, 0));
    }

    [Fact]
    public void Retry_backoff_grows_but_stays_capped()
    {
        var options = new AutopilotOptions { RetryBaseDelaySeconds = 30, RetryMaxDelaySeconds = 900 };
        options.Normalize();

        var first = AutopilotRetryPolicy.ComputeBackoff(0, options);
        var second = AutopilotRetryPolicy.ComputeBackoff(1, options);
        var huge = AutopilotRetryPolicy.ComputeBackoff(64, options);

        Assert.True(first.TotalSeconds >= 30);
        Assert.True(second > first);
        Assert.True(huge.TotalSeconds <= options.RetryMaxDelaySeconds);
        Assert.True(AutopilotRetryPolicy.ComputeBackoff(3, options).TotalSeconds <= options.RetryMaxDelaySeconds);
    }

    [Fact]
    public void Integration_gate_requires_all_evidence()
    {
        var options = new AutopilotOptions();
        options.Normalize();

        var complete = CompleteChecks();
        var full = BuildInput(complete);
        Assert.True(AutopilotSafetyPolicy.EvaluateIntegrationGate(full, options).Passed);

        var missingE2E = CompleteChecks();
        missingE2E[AutopilotChecks.BrowserE2E] = false;
        var e2e = AutopilotSafetyPolicy.EvaluateIntegrationGate(BuildInput(missingE2E), options);
        Assert.False(e2e.Passed);
        Assert.Contains(AutopilotChecks.BrowserE2E, e2e.FailedChecks);

        var missingMigrations = CompleteChecks();
        missingMigrations[AutopilotChecks.Migrations] = false;
        var migrations = AutopilotSafetyPolicy.EvaluateIntegrationGate(BuildInput(missingMigrations), options);
        Assert.False(migrations.Passed);
        Assert.Contains(AutopilotChecks.Migrations, migrations.FailedChecks);

        var missingTests = CompleteChecks();
        missingTests[AutopilotChecks.UnitTests] = false;
        Assert.False(AutopilotSafetyPolicy.EvaluateIntegrationGate(BuildInput(missingTests), options).Passed);

        var incomplete = BuildInput(complete) with { AllTasksSucceeded = false };
        Assert.False(AutopilotSafetyPolicy.EvaluateIntegrationGate(incomplete, options).Passed);

        var blocking = BuildInput(complete) with { HasBlockingFailure = true };
        Assert.False(AutopilotSafetyPolicy.EvaluateIntegrationGate(blocking, options).Passed);

        var shaMismatch = BuildInput(complete) with { CandidateShaMatches = false };
        Assert.False(AutopilotSafetyPolicy.EvaluateIntegrationGate(shaMismatch, options).Passed);
    }

    [Fact]
    public void Release_gate_requires_integration_plus_security_and_accounting()
    {
        var checks = CompleteChecks();
        checks[AutopilotChecks.Security] = false;
        var security = AutopilotSafetyPolicy.EvaluateReleaseGate(BuildInput(checks), integrationGatePassed: true);
        Assert.False(security.Passed);
        Assert.Contains(AutopilotChecks.Security, security.FailedChecks);

        var accountingChecks = CompleteChecks();
        accountingChecks[AutopilotChecks.Accounting] = false;
        var accounting = AutopilotSafetyPolicy.EvaluateReleaseGate(BuildInput(accountingChecks), integrationGatePassed: true);
        Assert.False(accounting.Passed);
        Assert.Contains(AutopilotChecks.Accounting, accounting.FailedChecks);

        Assert.False(AutopilotSafetyPolicy.EvaluateReleaseGate(BuildInput(CompleteChecks()), integrationGatePassed: false).Passed);
        Assert.True(AutopilotSafetyPolicy.EvaluateReleaseGate(BuildInput(CompleteChecks()), integrationGatePassed: true).Passed);
    }

    [Fact]
    public void Destructive_operations_are_never_permitted()
    {
        foreach (var action in AutopilotForbiddenActions.All)
        {
            Assert.True(AutopilotSafetyPolicy.IsForbiddenAction(action));
            Assert.False(AutopilotSafetyPolicy.CanPerformDestructiveDatabaseAction(action));
        }

        Assert.Contains(AutopilotForbiddenActions.ForcePush, AutopilotForbiddenActions.All);
        Assert.Contains(AutopilotForbiddenActions.ResetProductionDatabase, AutopilotForbiddenActions.All);
        Assert.False(AutopilotSafetyPolicy.CanPerformDestructiveDatabaseAction("reset_production_database"));
    }

    [Fact]
    public void Paid_capabilities_are_treated_as_a_human_gate()
    {
        var off = new AutopilotOptions();
        off.Normalize();
        Assert.False(AutopilotSafetyPolicy.PaidCapabilitiesEnabled(off));
        Assert.True(AutopilotHumanDecisionPolicy.RequiresHumanDecision(AutopilotHumanDecisions.EnableCharging));
        Assert.True(AutopilotHumanDecisionPolicy.RequiresHumanDecision(AutopilotHumanDecisions.EnablePaidProviders));

        var chargingOn = new AutopilotOptions { ChargingEnabled = true };
        chargingOn.Normalize();
        Assert.True(AutopilotSafetyPolicy.PaidCapabilitiesEnabled(chargingOn));
    }

    [Fact]
    public void Human_decisions_cover_every_mandated_category()
    {
        foreach (var expected in new[]
                 {
                     AutopilotHumanDecisions.Pricing,
                     AutopilotHumanDecisions.DestructiveSchema,
                     AutopilotHumanDecisions.SecurityAmbiguity,
                     AutopilotHumanDecisions.ProductDirection,
                     AutopilotHumanDecisions.EnableCharging,
                     AutopilotHumanDecisions.EnablePaidProviders,
                 })
        {
            Assert.Contains(expected, AutopilotHumanDecisions.All);
            Assert.True(AutopilotHumanDecisionPolicy.RequiresHumanDecision(expected));
        }

        Assert.False(AutopilotHumanDecisionPolicy.RequiresHumanDecision("something_else"));
    }

    [Fact]
    public void Production_configuration_refuses_unsafe_autopilot_settings()
    {
        var environment = new StubEnvironment("Production");
        var safe = SafeProductionConfiguration();
        ProductionConfigurationValidator.Validate(BuildConfiguration(safe), environment);

        var charging = SafeProductionConfiguration();
        charging["Autopilot:ChargingEnabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(charging), environment));

        var paidProviders = SafeProductionConfiguration();
        paidProviders["Autopilot:PaidProvidersEnabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(paidProviders), environment));

        // An enabled controller never boots without the server-side signing secret,
        // whether it is simulating (Stage 1) or live (Stage 2).
        Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(EnabledConfiguration(dryRun: true)), environment));
        Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(EnabledConfiguration(dryRun: false)), environment));

        WithSigningSecret(() =>
        {
            // Signature verification can never be relaxed while the controller is enabled.
            var unsigned = EnabledConfiguration(dryRun: true);
            unsigned["Autopilot:RequireSignedEvents"] = "false";
            Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(unsigned), environment));

            // Paid capabilities stay refused in every staged posture.
            var stagedCharging = EnabledConfiguration(dryRun: true);
            stagedCharging["Autopilot:ChargingEnabled"] = "true";
            Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(stagedCharging), environment));

            var stagedPaidProviders = EnabledConfiguration(dryRun: true);
            stagedPaidProviders["Autopilot:PaidProvidersEnabled"] = "true";
            Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate(BuildConfiguration(stagedPaidProviders), environment));
        });
    }

    [Fact]
    public void Production_configuration_allows_staged_dry_run_activation()
    {
        var environment = new StubEnvironment("Production");

        // Stage 0: the shipped dark posture boots without any signing secret.
        ProductionConfigurationValidator.Validate(BuildConfiguration(SafeProductionConfiguration()), environment);

        WithSigningSecret(() =>
        {
            // Stage 1: enabled and simulating. This supervised dry-run posture is the
            // intended next activation stage and must boot in production.
            ProductionConfigurationValidator.Validate(BuildConfiguration(EnabledConfiguration(dryRun: true)), environment);

            // An omitted DryRun value falls back to the safe simulating posture.
            ProductionConfigurationValidator.Validate(BuildConfiguration(EnabledConfiguration(dryRun: null)), environment);

            // Stage 2: live execution stays reachable, but only by setting DryRun=false explicitly.
            ProductionConfigurationValidator.Validate(BuildConfiguration(EnabledConfiguration(dryRun: false)), environment);
        });
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Signature_verification_rejects_missing_and_replayed_events()
    {
        var options = new AutopilotOptions { RequireSignedEvents = true, SignatureToleranceSeconds = 300 };
        options.Normalize();
        var authenticator = new AutopilotEventAuthenticator(options);
        var now = DateTime.UtcNow;
        var payload = "{\"waveKey\":\"wave-6\"}";

        Assert.False(authenticator.Verify(null, "0", payload, now).Valid);
        Assert.False(authenticator.Verify("sha256=abc", null, payload, now).Valid);
        Assert.False(authenticator.Verify("sha256=abc", $"{new DateTimeOffset(now.AddMinutes(-30)).ToUnixTimeSeconds()}", payload, now).Valid);

        var unsignedDisabled = new AutopilotOptions { RequireSignedEvents = false };
        unsignedDisabled.Normalize();
        Assert.True(new AutopilotEventAuthenticator(unsignedDisabled).Verify(null, null, payload, now).Valid);

        Assert.Equal(64, authenticator.ComputePayloadHash(payload).Length);
    }

    [Fact]
    public void Idempotency_keys_are_stable_and_provider_neutral()
    {
        var first = AutopilotEventIntake.BuildIdempotencyKey("completion-bridge", "evt-123");
        var second = AutopilotEventIntake.BuildIdempotencyKey("completion-bridge", "evt-123");
        Assert.Equal(first, second);
        Assert.Equal("autopilot:completion-bridge:evt-123", first);
    }

    private static AutopilotGateInput BuildInput(Dictionary<string, bool> checks) =>
        new(AllTasksSucceeded: true, HasBlockingFailure: false, CandidateShaMatches: true, Checks: checks);

    private static Dictionary<string, bool> CompleteChecks() => new(StringComparer.OrdinalIgnoreCase)
    {
        [AutopilotChecks.Build] = true,
        [AutopilotChecks.UnitTests] = true,
        [AutopilotChecks.Typecheck] = true,
        [AutopilotChecks.Lint] = true,
        [AutopilotChecks.BrowserE2E] = true,
        [AutopilotChecks.Migrations] = true,
        [AutopilotChecks.Security] = true,
        [AutopilotChecks.Accounting] = true,
        [AutopilotChecks.IntegrationComplete] = true,
    };

    private static Dictionary<string, string?> SafeProductionConfiguration() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ConnectionStrings:Postgres"] = "Host=db.internal;Port=5432;Database=taslim;Username=taslim;Password=strong",
        ["AllowedOrigins:0"] = "https://app.taslim.ai",
        ["Files:StorageProvider"] = "S3Compatible",
        ["Files:S3Endpoint"] = "https://s3.example.com",
        ["Files:S3Region"] = "auto",
        ["Files:S3Bucket"] = "taslim",
        ["Files:S3AccessKey"] = "key",
        ["Files:S3SecretKey"] = "secret",
        ["Billing:CustomerChargingEnabled"] = "false",
        ["Autopilot:ChargingEnabled"] = "false",
        ["Autopilot:PaidProvidersEnabled"] = "false",
        ["Autopilot:Enabled"] = "false",
        ["Autopilot:RequireSignedEvents"] = "true",
    };

    /// <summary>
    /// Dedicated variable name so tests never read or write the real production
    /// secret name, and so the "secret absent" case stays deterministic.
    /// </summary>
    private const string TestSigningSecretVariable = "AUTOPILOT_TEST_SIGNING_SECRET";

    private static Dictionary<string, string?> EnabledConfiguration(bool? dryRun)
    {
        var values = SafeProductionConfiguration();
        values["Autopilot:Enabled"] = "true";
        values["Autopilot:RequireSignedEvents"] = "true";
        values["Autopilot:SigningSecretEnvironmentVariable"] = TestSigningSecretVariable;
        if (dryRun is not null) values["Autopilot:DryRun"] = dryRun.Value ? "true" : "false";
        return values;
    }

    private static void WithSigningSecret(Action assertion)
    {
        Environment.SetEnvironmentVariable(TestSigningSecretVariable, "test-only-value");
        try
        {
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestSigningSecretVariable, null);
        }
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Taslim.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
