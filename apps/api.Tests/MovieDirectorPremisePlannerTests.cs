using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorPremisePlannerTests
{
    [Fact]
    public void Last_seed_fixture_develops_a_grounded_concise_premise_without_mutating_source()
    {
        const string originalPremise = "A young farmer must decide what to do with her grandfather's last seed.";
        var context = LastSeedContext(originalPremise, durationSeconds: 180);
        var planner = new DirectorStoryProposalPlanner();

        var proposal = planner.Build(new DirectorProposalRequest { StoryAction = DirectorStoryActionTypes.DevelopPremise }, context);
        var review = proposal.Review;
        var developed = review.Changes.Single().ProposedContent;

        Assert.NotEqual(originalPremise, developed);
        Assert.Contains("Mara", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("young farmer", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drought", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last seed", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grandfather", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("community", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plant", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rain", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hope", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choice", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("creative_additions_goal_stakes_choice_emotional_engine_theme", review.Findings);
        Assert.Contains("premise_source_facts_preserved", review.Findings);
        Assert.Contains("premise_creative_additions_explicit", review.Findings);
        Assert.Equal(originalPremise, review.Changes.Single().ExistingContent);
    }

    [Fact]
    public void Generic_premise_boilerplate_is_replaced_by_a_grounded_proposal()
    {
        const string genericPremise = "A protagonist faces a defining choice.";
        var context = LastSeedContext(genericPremise, durationSeconds: 45);
        var planner = new DirectorStoryProposalPlanner();

        var proposal = planner.Build(new DirectorProposalRequest { StoryAction = DirectorStoryActionTypes.DevelopPremise }, context);
        var developed = proposal.Review.Changes.Single().ProposedContent;

        Assert.Contains("generic_premise_boilerplate_replaced", proposal.Review.Findings);
        Assert.DoesNotContain("The choice must carry a consequence.", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mara", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("community", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(genericPremise, proposal.Review.Changes.Single().ExistingContent);
    }

    [Fact]
    public void Premise_mode_respects_language_and_short_duration_contracts()
    {
        var context = LastSeedContext("A protagonist faces a defining choice.", durationSeconds: 45) with { Language = "ar" };
        var proposal = new DirectorStoryProposalPlanner().Build(new DirectorProposalRequest { StoryAction = DirectorStoryActionTypes.DevelopPremise }, context);

        var developed = proposal.Review.Changes.Single().ProposedContent;
        Assert.Contains("premise_language_ar", proposal.Review.Findings);
        Assert.Contains("premise_duration_short_form", proposal.Review.Findings);
        Assert.DoesNotContain("The emotional engine", developed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("الأمل", developed, StringComparison.Ordinal);
    }

    private static DirectorStoryBoundedContextDto LastSeedContext(string premise, int durationSeconds) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "A young farmer in a drought-stricken village guards her grandfather's last seed while the community opposes her plan to plant it; returning rain offers fragile hope.",
        new DirectorGuideContext("grounded rural naturalism", "observational", "earth tones", "wind and silence", "Keep the village and family history grounded.", 1, true),
        new DirectorStoryRevisionContext(Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Draft, MovieStoryAuthorship.Human, premise, "", "", "", []),
        null,
        null,
        [],
        [new DirectorCharacterContext("Mara", "A young farmer entrusted with her grandfather's last seed.", null, null)],
        [new DirectorLocationContext("Drought-stricken village", "A village waiting for rain.", null)],
        DateTime.UtcNow,
        1,
        durationSeconds,
        "en");
}
