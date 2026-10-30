using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobErrorMessagesTests
{
    [Theory]
    [InlineData(
        GenerationJobErrorCodes.ResearchSourceUnavailable,
        "No usable research sources are available. Add or choose another source, then try again.")]
    [InlineData(
        GenerationJobErrorCodes.ResearchSourceExtractionFailed,
        "One or more research sources could not be read. Choose a readable source and try again.")]
    public void For_research_source_failures_returns_actionable_bounded_messages(string code, string expectedMessage)
    {
        var message = GenerationJobErrorMessages.For(code);

        Assert.Equal(expectedMessage, message);
        Assert.DoesNotContain(code, message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack trace", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void For_unknown_research_error_codes_keeps_the_generic_safe_fallback()
    {
        var message = GenerationJobErrorMessages.For("RESEARCH_UNKNOWN_PRIVATE_DETAIL");

        Assert.Equal("The research report could not be generated. Please try again.", message);
        Assert.DoesNotContain("PRIVATE_DETAIL", message!, StringComparison.OrdinalIgnoreCase);
    }
}
