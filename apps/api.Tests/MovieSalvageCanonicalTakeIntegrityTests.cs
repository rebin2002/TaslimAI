using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSalvageCanonicalTakeIntegrityTests
{
    [Fact]
    public void Regeneration_fallback_does_not_claim_to_preserve_the_canonical_take()
    {
        var plan = new MovieSalvageDirector().Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Generic,
            Context = new MovieSalvageEditContext(),
        });

        var regeneration = Assert.Single(plan.Options);
        Assert.Equal(MovieSalvageActionTypes.Regenerate, regeneration.Action);
        Assert.False(regeneration.PreservesCanonicalTake);
    }
}
