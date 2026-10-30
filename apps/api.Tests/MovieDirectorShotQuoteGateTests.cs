using Taslim.Api.Movies;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorShotQuoteGateTests
{
    [Fact]
    public async Task Director_generate_shot_fails_closed_without_exact_shot_quote_and_price_confirmation()
    {
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(new FakeProviderScenarioCatalog(), calls);
        var executor = new MovieDirectorActionExecutor(provider);
        var action = new DirectorAction
        {
            ActionType = DirectorActionTypes.GenerateShot,
            PayloadJson = "{}",
        };

        var result = await executor.ExecuteAsync(action, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal("MOVIE_SCENE_QUOTE_REQUIRED", result.FailureCode);
        Assert.Contains("exact shot-duration provider/model quote", result.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("maximum USD confirmation", result.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }
}
