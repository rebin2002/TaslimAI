using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieReferenceReadinessTests
{
    [Fact]
    public void An_unrelated_project_prop_does_not_satisfy_an_explicit_shot_prop_requirement()
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara opens the brass compass before crossing the riverbed.",
            [Prop("notebook")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, result.State);
        Assert.True(result.BlocksGeneration);
        Assert.False(result.OverrideAllowed);
    }

    [Fact]
    public void A_named_canonical_project_prop_satisfies_the_shot_prop_requirement()
    {
        var propId = Guid.NewGuid();
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara opens the brass compass before crossing the riverbed.",
            [Prop("Brass Compass", propId)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.False(result.BlocksGeneration);
        Assert.Equal(propId, result.SourceId);
    }

    [Theory]
    [InlineData("A monkey crosses the room.", "key")]
    [InlineData("The keyboard player tunes between takes.", "key")]
    [InlineData("She showed genuine compassion to the courier.", "compass")]
    public void An_unrelated_substring_cannot_falsely_satisfy_a_canonical_prop_reference(string shotText, string propName)
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(shotText, [Prop(propName)], important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.False(result.BlocksGeneration);
        Assert.Null(result.SourceId);
    }

    [Fact]
    public void A_multiword_prop_name_requires_its_contiguous_token_sequence()
    {
        var propId = Guid.NewGuid();
        var contiguous = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara hides the brass compass under the coat.",
            [Prop("Brass Compass", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, contiguous.State);
        Assert.Equal(propId, contiguous.SourceId);

        var separated = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "The compass is brass.",
            [Prop("Brass Compass", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Missing, separated.State);
        Assert.True(separated.BlocksGeneration);
    }

    [Fact]
    public void A_non_latin_prop_name_matches_only_at_word_boundaries()
    {
        var propId = Guid.NewGuid();
        var matched = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara lifts the کۆشک from the crate.",
            [Prop("کۆشک", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, matched.State);
        Assert.Equal(propId, matched.SourceId);

        var unrelated = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara sketches the landscape from the hilltop.",
            [Prop("کۆشک", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, unrelated.State);
        Assert.Null(unrelated.SourceId);
    }

    [Fact]
    public void The_director_empty_prop_requirement_header_is_not_a_real_prop_requirement()
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: . VFX: none. Production notes: none",
            [Prop("notebook")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.False(result.BlocksGeneration);
        Assert.True(result.OverrideAllowed);
        Assert.Null(result.SourceId);
    }

    [Fact]
    public void The_director_prop_requirement_header_still_counts_when_props_are_listed()
    {
        var propId = Guid.NewGuid();
        var satisfied = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: notebook. VFX: none. Production notes: none",
            [Prop("notebook", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, satisfied.State);
        Assert.Equal(propId, satisfied.SourceId);

        var unsatisfied = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: notebook. VFX: none. Production notes: none",
            [Prop("brass compass", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Missing, unsatisfied.State);
        Assert.True(unsatisfied.BlocksGeneration);
    }

    [Fact]
    public void A_genuine_plural_prop_mention_still_blocks_a_shot_without_a_canonical_match()
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "The crew arranges props on the table between takes.",
            [Prop("notebook")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, result.State);
        Assert.True(result.BlocksGeneration);
    }

    [Theory]
    [InlineData("Props")]
    [InlineData("Important")]
    public void A_prop_named_after_the_placeholder_header_words_is_not_falsely_attached(string propName)
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: . VFX: none. Production notes: none",
            [Prop(propName)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.Null(result.SourceId);
    }

    [Fact]
    public void Enumerated_director_prop_requirements_fail_closed_when_a_named_prop_is_missing()
    {
        var compassId = Guid.NewGuid();
        var partial = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: compass, notebook. VFX: none. Production notes: none",
            [Prop("Compass", compassId)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, partial.State);
        Assert.True(partial.BlocksGeneration);
        Assert.False(partial.OverrideAllowed);

        var complete = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: compass, notebook. VFX: none. Production notes: none",
            [Prop("Compass", compassId), Prop("Notebook")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, complete.State);
        Assert.Equal(compassId, complete.SourceId);
    }

    [Fact]
    public void An_enumerated_requirement_is_satisfied_by_a_more_specific_canonical_prop_name()
    {
        var propId = Guid.NewGuid();
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: compass. VFX: none. Production notes: none",
            [Prop("Brass Compass", propId)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.Equal(propId, result.SourceId);
    }

    [Fact]
    public void A_generic_canonical_prop_does_not_satisfy_a_more_specific_enumerated_requirement()
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: brass compass. VFX: none. Production notes: none",
            [Prop("Compass")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, result.State);
        Assert.True(result.BlocksGeneration);
        Assert.Null(result.SourceId);
    }

    [Fact]
    public void One_canonical_prop_cannot_satisfy_two_enumerated_requirements()
    {
        var single = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: compass, compass. VFX: none. Production notes: none",
            [Prop("Compass")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, single.State);
        Assert.True(single.BlocksGeneration);

        var brass = Guid.NewGuid();
        var silver = Guid.NewGuid();
        var distinct = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Important props: brass compass, silver compass. VFX: none. Production notes: none",
            [Prop("Brass Compass", brass), Prop("Silver Compass", silver)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, distinct.State);
        Assert.Equal(brass, distinct.SourceId);
    }

    [Fact]
    public void Combining_marks_fold_inside_tokens_for_unicode_prop_matching()
    {
        var propId = Guid.NewGuid();
        var decomposedAgainstPlain = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara sips a cafe\u0301 before the crossing.",
            [Prop("Cafe", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, decomposedAgainstPlain.State);
        Assert.Equal(propId, decomposedAgainstPlain.SourceId);

        var decomposedAgainstDecomposed = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "Mara sips a cafe\u0301 before the crossing.",
            [Prop("cafe\u0301", propId)],
            important: true);
        Assert.Equal(MovieReferenceReadinessItemStates.Complete, decomposedAgainstDecomposed.State);
        Assert.Equal(propId, decomposedAgainstDecomposed.SourceId);
    }

    [Theory]
    [InlineData("玛拉拿起指南针然后离开。", "指南针")]
    [InlineData("마라가 나침반을 든다", "나침반")]
    [InlineData("มาราหยิบสว่านออกจากกระเป๋า", "สว่าน")]
    public void A_prop_name_in_a_spaceless_script_matches_inside_an_unsegmented_sentence(string shotText, string propName)
    {
        var propId = Guid.NewGuid();
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(shotText, [Prop(propName, propId)], important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.Equal(propId, result.SourceId);
    }

    [Fact]
    public void Arabic_prop_names_match_regardless_of_diacritic_marks()
    {
        var propId = Guid.NewGuid();
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "أدارت مفتاحَ الحرف بحذر.",
            [Prop("مفتاح", propId)],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Complete, result.State);
        Assert.Equal(propId, result.SourceId);
    }

    [Fact]
    public void A_chinese_prop_mention_without_a_canonical_match_blocks_an_important_shot()
    {
        var result = MovieReferenceReadinessPolicy.EvaluatePropReference(
            "玛拉需要一件道具放在桌上。",
            [Prop("笔记本")],
            important: true);

        Assert.Equal(MovieReferenceReadinessItemStates.Missing, result.State);
        Assert.True(result.BlocksGeneration);
    }

    [Fact]
    public void Ten_reference_dimensions_are_aggregated_into_an_explainable_blocked_result()
    {
        var items = new[]
        {
            Complete("character"),
            Complete("wardrobe"),
            Blocked("location", overrideAllowed: false),
            Complete("lighting"),
            Warning("props"),
            Complete("spatial_orientation"),
            Complete("camera_plan"),
            Complete("dialogue"),
            Blocked("continuity", overrideAllowed: false),
            Warning("transition_out"),
        };

        Assert.Equal(60, MovieReferenceReadinessPolicy.CalculatePercentage(items));
        Assert.Equal(MovieReferenceReadinessStatuses.Blocked, MovieReferenceReadinessPolicy.DetermineStatus(items));
        Assert.False(MovieReferenceReadinessPolicy.IsOverrideAllowed(items));
    }

    [Fact]
    public void A_blocker_is_safely_overridable_only_when_every_blocked_item_allows_it()
    {
        var safe = new[]
        {
            Complete("character"),
            Blocked("lighting", overrideAllowed: true),
            Blocked("spatial_orientation", overrideAllowed: true),
        };
        var unsafeItems = safe.Append(Blocked("location", overrideAllowed: false)).ToArray();

        Assert.True(MovieReferenceReadinessPolicy.IsOverrideAllowed(safe));
        Assert.False(MovieReferenceReadinessPolicy.IsOverrideAllowed(unsafeItems));
    }

    [Fact]
    public void Warnings_need_review_but_do_not_block_generation()
    {
        var items = new[]
        {
            Complete("character"),
            Warning("dialogue"),
            Warning("transition_out"),
        };

        Assert.Equal(33, MovieReferenceReadinessPolicy.CalculatePercentage(items));
        Assert.Equal(MovieReferenceReadinessStatuses.NeedsReview, MovieReferenceReadinessPolicy.DetermineStatus(items));
        Assert.DoesNotContain(items, item => item.BlocksGeneration);
    }

    private static MovieReferenceReadinessItemDto Complete(string key) =>
        new(key, key, MovieReferenceReadinessItemStates.Complete, true, false, true, $"{key} is complete.");

    private static MovieReferenceReadinessItemDto Warning(string key) =>
        new(key, key, MovieReferenceReadinessItemStates.Warning, false, false, true, $"{key} needs review.");

    private static MovieReferenceReadinessItemDto Blocked(string key, bool overrideAllowed) =>
        new(key, key, MovieReferenceReadinessItemStates.Missing, true, true, overrideAllowed, $"{key} is blocked.");

    private static MovieProductionPropReferenceDto Prop(string name, Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            name,
            $"A {name} used in the shot.",
            null,
            null,
            null,
            null,
            new MovieProductionReferenceProvenanceDto("test", null, null, "test-hash", "test", DateTime.UnixEpoch));
}
