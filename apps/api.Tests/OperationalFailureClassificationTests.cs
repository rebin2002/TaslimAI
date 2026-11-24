using Microsoft.Extensions.Logging;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OperationalFailureClassificationTests
{
    [Theory]
    [InlineData("IMAGE_PROVIDER_UNAVAILABLE", "provider", true)]
    [InlineData("DOCUMENT_PROVIDER_UNSUPPORTED_REQUEST", "provider", false)]
    [InlineData("DOCUMENT_STORAGE_FAILED", "storage", true)]
    [InlineData("IMAGE_OUTPUT_INVALID", "output", true)]
    [InlineData("JOB_CANCELLED", "cancelled", false)]
    [InlineData("JOB_POISONED", "recovery", false)]
    [InlineData("IMAGE_REQUEST_INVALID", "validation", false)]
    [InlineData("UNRECOGNIZED_FAILURE", "internal", false)]
    // Post-billing music delivery failures are isolated from the retryable provider class, while
    // the ordinary music provider outage keeps its existing (log-only) retryable classification.
    [InlineData("MUSIC_OUTPUT_RETRIEVAL_FAILED", "delivery", false)]
    [InlineData("MUSIC_RETRIEVAL_RETRY_BLOCKED", "delivery", false)]
    // Lease expiry with an unknown submission outcome is terminalized, never auto-re-dispatched.
    [InlineData("MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED", "provider", false)]
    [InlineData("MUSIC_SUBMISSION_RETRY_BLOCKED", "provider", false)]
    // F-36: cancelling an ACCEPTED submission is the same unknown-outcome state as losing contact with
    // it, and cancelling after a completed output is the same delivery state as a timed-out read.
    [InlineData("MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION", "provider", false)]
    [InlineData("MUSIC_CANCELLED_UNDELIVERED_OUTPUT", "delivery", false)]
    [InlineData("MUSIC_PROVIDER_UNAVAILABLE", "provider", true)]
    public void Classifier_returns_bounded_operational_categories(string code, string category, bool retryable)
    {
        var result = GenerationFailureClassifier.Classify(code);

        Assert.Equal(code, result.Code);
        Assert.Equal(category, result.Category);
        Assert.Equal(retryable, result.Retryable);
    }

    [Fact]
    public void Classifier_never_uses_exception_text_for_missing_codes()
    {
        var result = GenerationFailureClassifier.Classify(null);

        Assert.Equal("UNKNOWN_FAILURE", result.Code);
        Assert.Equal("internal", result.Category);
        Assert.DoesNotContain("Exception", result.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Job_scope_links_request_job_and_retry_lineage_without_payload_values()
    {
        var logger = new CapturingLogger();
        var job = new GenerationJob
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            RequestId = "request-123",
            RetryOfJobId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            JobType = GenerationJobTypes.SystemTest,
        };

        using (GenerationJobOperationalScope.Begin(logger, job)) { }

        var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(logger.ScopeState);
        var properties = values.ToDictionary(item => item.Key, item => item.Value);
        Assert.Equal("request-123", properties["CorrelationId"]);
        Assert.Equal("request-123", properties["RequestId"]);
        Assert.Equal(job.Id, properties["JobId"]);
        Assert.Equal(GenerationJobTypes.SystemTest, properties["JobType"]);
        Assert.Equal(job.RetryOfJobId, properties["RetryOfJobId"]);
        Assert.DoesNotContain(properties.Values, value => value?.ToString()?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class CapturingLogger : ILogger
    {
        public object? ScopeState { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            ScopeState = state;
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
