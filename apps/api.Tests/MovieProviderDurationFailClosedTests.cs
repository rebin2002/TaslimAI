using System.Reflection;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-free regression for #263/#361's shared worker publication seam.
// The parser is intentionally private; reflection avoids expanding production API.
public sealed class MovieProviderDurationFailClosedTests
{
    private static int? Parse(string? metadata)
    {
        var parser = typeof(GenerationJobWorker).GetMethod(
            "ReadDurationSeconds", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parser);
        return (int?)parser!.Invoke(null, [metadata]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"durationSeconds\":null}")]
    [InlineData("{\"durationSeconds\":\"8\"}")]
    [InlineData("{\"durationSeconds\":true}")]
    [InlineData("{\"durationSeconds\":[]}")]
    [InlineData("{\"durationSeconds\":{}}")]
    [InlineData("{\"durationSeconds\":8.5}")]
    [InlineData("{\"durationSeconds\":0}")]
    [InlineData("{\"durationSeconds\":-1}")]
    [InlineData("{\"durationSeconds\":3601}")]
    [InlineData("{\"durationSeconds\":2147483648}")]
    [InlineData("{\"durationSeconds\":}")]
    public void Missing_malformed_or_out_of_range_measured_duration_is_rejected(string? metadata)
    {
        Assert.Null(Parse(metadata));
    }

    [Theory]
    [InlineData("{\"durationSeconds\":1}", 1)]
    [InlineData("{\"durationSeconds\":8}", 8)]
    [InlineData("{\"durationSeconds\":3600}", 3600)]
    public void Bounded_numeric_measured_duration_is_accepted(string metadata, int expected)
    {
        Assert.Equal(expected, Parse(metadata));
    }
}
