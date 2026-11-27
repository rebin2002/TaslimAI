using Taslim.Api.Email;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordRecoveryLeasePolicyTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(59)]
    public void Recovery_readiness_fails_closed_below_smtp_timeout_plus_processing_margin(int leaseSeconds)
    {
        var options = CreateReadyOptions();
        options.LeaseSeconds = leaseSeconds;

        Assert.False(options.IsReady(isProduction: false, new ConfiguredEmailSender()));
    }

    [Fact]
    public void Recovery_readiness_accepts_the_minimum_configured_lease()
    {
        var options = CreateReadyOptions();
        options.LeaseSeconds = PasswordRecoveryOptions.MinimumLeaseSeconds;

        Assert.Equal(15, PasswordRecoveryOptions.SmtpSendTimeoutSeconds);
        Assert.Equal(45, PasswordRecoveryOptions.LeaseProcessingMarginSeconds);
        Assert.Equal(60, PasswordRecoveryOptions.MinimumLeaseSeconds);
        Assert.True(options.IsReady(isProduction: false, new ConfiguredEmailSender()));
    }

    [Fact]
    public void Default_lease_meets_the_timeout_plus_margin_floor()
    {
        var options = CreateReadyOptions();

        Assert.Equal(PasswordRecoveryOptions.MinimumLeaseSeconds, options.LeaseSeconds);
        Assert.True(options.IsReady(isProduction: false, new ConfiguredEmailSender()));
    }

    private static PasswordRecoveryOptions CreateReadyOptions() => new()
    {
        Enabled = true,
        PublicBaseUrl = "http://localhost:3000",
    };

    private sealed class ConfiguredEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
