using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, deterministic regression for the Director Story premise renderer.
///
/// LimitPremise caps a rendered premise by slicing `value[..1_797]` and appending an ellipsis, so
/// the last KEPT code unit is index 1_796. The brief is user-authored text that is spliced in at
/// the front of the premise, so that code unit can be the high half of a surrogate pair. Cutting
/// there returns and persists a premise whose JSON string content ends in a dangling high
/// surrogate, which is the same defect already closed for the document, search, social and
/// generated-asset boundaries: those gates move the cut one code unit earlier instead.
///
/// DirectorStoryProposalPlanner.Build is a pure in-process function: no provider, database,
/// workspace, clock or randomness is involved.
/// </summary>
public sealed class MovieDirectorPremiseSurrogateBoundaryTests
{
    // U+1F3AC CLAPPER BOARD = high surrogate D83C + low surrogate DF9C.
    private const string Clapper = "\U0001F3AC";

    // LimitPremise keeps this many code units and then appends the ellipsis under the 1_800 cap.
    private const int KeptCodeUnits = 1_797;
    private const int Ceiling = 1_800;
    private const int LastKeptIndex = KeptCodeUnits - 1;

    [Fact]
    public void Premise_cut_never_ends_on_a_dangling_high_surrogate()
    {
        // An unbroken filler run of exactly LastKeptIndex characters puts the pair opener on the
        // last kept code unit, so the naive [..1_797] slice separates the pair and the ellipsis
        // lands immediately after a stranded high surrogate.
        var head = new string('a', LastKeptIndex) + Clapper;

        var premise = DevelopPremise(Brief(head));

        Assert.True(premise.Length <= Ceiling, $"Premise exceeded the {Ceiling} code unit ceiling: {premise.Length}.");
        Assert.EndsWith("…", premise, StringComparison.Ordinal);
        Assert.DoesNotContain(Clapper, premise, StringComparison.Ordinal);
        AssertHoldsWellFormedText(premise);
    }

    [Fact]
    public void Premise_cut_keeps_every_complete_character_that_fits()
    {
        // Where the pair already closes inside the kept prefix, nothing may be shaved off: the
        // surrogate guard moves the cut only when the prefix would otherwise end on a high half.
        var head = new string('a', LastKeptIndex - 1) + Clapper;

        var premise = DevelopPremise(Brief(head));

        Assert.True(premise.Length <= Ceiling);
        Assert.StartsWith(head, premise, StringComparison.Ordinal);
        AssertHoldsWellFormedText(premise);
    }

    [Fact]
    public void Premise_that_fits_the_ceiling_is_not_truncated()
    {
        var premise = DevelopPremise(Brief("A guarded witness chooses truth over safety."));

        Assert.False(premise.EndsWith("…", StringComparison.Ordinal));
        Assert.True(premise.Length <= Ceiling);
        Assert.Contains("witness", premise, StringComparison.OrdinalIgnoreCase);
        AssertHoldsWellFormedText(premise);
    }

    // The rendered premise is spliced as "{brief}. {narrative}"; the trailing filler guarantees the
    // whole string clears the 1_800 ceiling so the cut is always taken.
    private static string Brief(string head) => $"{head} {(head.Length > 40 ? string.Empty : "witness ")}and the choice stays costly for everyone who depends on it.";

    private static string DevelopPremise(string brief)
    {
        var context = new DirectorStoryBoundedContextDto(
            MovieProjectId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            MovieBrief: brief,
            Guide: new DirectorGuideContext(
                "grounded naturalism", "observational", "earth tones", "wind and silence",
                "Keep the story grounded."),
            CurrentRevision: new DirectorStoryRevisionContext(
                Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Draft, MovieStoryAuthorship.Human,
                "", "", "", "", Array.Empty<DirectorScreenplaySceneContext>()),
            ApprovedRevision: null,
            TargetScene: null,
            RelevantMovieScenes: Array.Empty<DirectorSceneContext>(),
            RelevantCharacters: Array.Empty<DirectorCharacterContext>(),
            RelevantWorldReferences: Array.Empty<DirectorLocationContext>(),
            AssembledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContextVersion: 1,
            DurationSeconds: 180,
            Language: "en");

        var plan = new DirectorStoryProposalPlanner().Build(
            new DirectorProposalRequest { StoryAction = DirectorStoryActionTypes.DevelopPremise },
            context);

        return Assert.Single(plan.Review.Changes).ProposedContent!;
    }

    /// <summary>
    /// Walks the string as UTF-16 and fails on any surrogate that is not part of a well-formed
    /// high/low pair, which is what the persisted JSON string content must never contain.
    /// </summary>
    private static void AssertHoldsWellFormedText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var unit = value[index];
            if (char.IsHighSurrogate(unit))
            {
                Assert.True(index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    $"Dangling high surrogate at index {index}.");
                index++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(unit), $"Dangling low surrogate at index {index}.");
        }
    }
}
