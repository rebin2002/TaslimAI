using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Matrix;

/// <summary>Why a pair of claims landed where it did.</summary>
public static class StanceReasonCodes
{
    public const string NoClaimOnOneSide = "STANCE_ONE_SIDE_SILENT";
    public const string DifferentSubject = "STANCE_DIFFERENT_SUBJECT";
    public const string DifferentPredicate = "STANCE_DIFFERENT_PREDICATE";
    public const string DifferentPopulation = "STANCE_DIFFERENT_POPULATION";
    public const string UnitMismatch = "STANCE_UNIT_MISMATCH";
    public const string DirectionUnknown = "STANCE_DIRECTION_UNKNOWN";
    public const string DirectionOpposed = "STANCE_DIRECTION_OPPOSED";
    public const string DirectionAligned = "STANCE_DIRECTION_ALIGNED";
    public const string IntervalOverlap = "STANCE_INTERVAL_OVERLAP";
    public const string IntervalGap = "STANCE_INTERVAL_GAP";
    public const string SignificanceDiffers = "STANCE_SIGNIFICANCE_DIFFERS";
    public const string DirectionalOnly = "STANCE_DIRECTIONAL_AGREEMENT_ONLY";
}

/// <summary>
/// Decision for one pair of claims, with the codes that produced it so the review
/// can state its reasoning rather than assert a verdict.
/// </summary>
public sealed record StanceComparison(
    ClaimRelation Relation,
    IReadOnlyList<string> ReasonCodes,
    string? Explanation);

/// <summary>
/// Decides whether two reported findings agree, contradict, or cannot be compared.
///
/// Contradiction is treated as a narrow, deliberate state. Two sources disagree
/// only when they address the same subject, predicate and population and report
/// opposed findings. Everything else is named for what it actually is: a study of a
/// different population is a coverage question, a percentage compared against an
/// odds ratio is not comparable, and a source that never studied the question is
/// silence, not dissent. Reporting silence as contradiction is how a review invents
/// a debate that the literature does not contain.
/// </summary>
public static class ClaimStanceComparer
{
    /// <summary>Compare two claims; either side may be absent.</summary>
    public static StanceComparison Compare(EvidenceClaim? left, EvidenceClaim? right)
    {
        var reasons = new List<string>();

        if (left is null || right is null)
        {
            reasons.Add(StanceReasonCodes.NoClaimOnOneSide);
            return new StanceComparison(ClaimRelation.NoEvidence, reasons,
                "Only one side reported a finding, which is a gap in coverage rather than a disagreement.");
        }

        if (!KeyEquals(left.SubjectKey, right.SubjectKey))
        {
            reasons.Add(StanceReasonCodes.DifferentSubject);
            return new StanceComparison(ClaimRelation.NotComparable, reasons,
                "The findings concern different subjects, so they cannot be placed against each other.");
        }

        if (!KeyEquals(left.PredicateKey, right.PredicateKey))
        {
            reasons.Add(StanceReasonCodes.DifferentPredicate);
            return new StanceComparison(ClaimRelation.NotComparable, reasons,
                "The findings measure different relationships, so they cannot be placed against each other.");
        }

        if (!string.IsNullOrWhiteSpace(left.PopulationKey) && !string.IsNullOrWhiteSpace(right.PopulationKey)
            && !KeyEquals(left.PopulationKey, right.PopulationKey))
        {
            reasons.Add(StanceReasonCodes.DifferentPopulation);
            return new StanceComparison(ClaimRelation.NotComparable, reasons,
                "The findings come from different populations. They may both be true within their own settings.");
        }

        if (left.Direction == EffectDirection.Unknown || right.Direction == EffectDirection.Unknown)
        {
            reasons.Add(StanceReasonCodes.DirectionUnknown);
            return new StanceComparison(ClaimRelation.Indeterminate, reasons,
                "At least one source does not report a direction, so no comparison can be made.");
        }

        var opposed = AreOpposed(left.Direction, right.Direction);
        var aligned = left.Direction == right.Direction
            || (left.Direction == EffectDirection.NoEffect && right.Direction == EffectDirection.NoEffect);

        // Direction alone decides the headline relation; magnitudes only qualify it.
        if (opposed)
        {
            reasons.Add(StanceReasonCodes.DirectionOpposed);

            if (left.ExcludesNull == false || right.ExcludesNull == false)
            {
                // One side reported a null result inside its interval, which softens
                // the clash into a difference the review must describe carefully.
                reasons.Add(StanceReasonCodes.SignificanceDiffers);
                return new StanceComparison(ClaimRelation.Indeterminate, reasons,
                    "The sources point opposite ways, but at least one reports an interval that includes no "
                        + "effect, so the difference may be one of precision rather than of finding.");
            }

            return new StanceComparison(ClaimRelation.Contradiction, reasons,
                "Both sources address the same question in the same population and report opposite findings.");
        }

        if (!aligned)
        {
            // Mixed against anything else cannot be ordered.
            reasons.Add(StanceReasonCodes.DirectionUnknown);
            return new StanceComparison(ClaimRelation.Indeterminate, reasons,
                "One source reports a mixed result, which cannot be reduced to agreement or disagreement.");
        }

        reasons.Add(StanceReasonCodes.DirectionAligned);

        if (left.Magnitude.HasValue != right.Magnitude.HasValue
            || (left.Magnitude.HasValue && right.Magnitude.HasValue
                && !SameUnit(left.Unit, right.Unit)))
        {
            reasons.Add(left.Magnitude.HasValue != right.Magnitude.HasValue
                ? StanceReasonCodes.DirectionalOnly
                : StanceReasonCodes.UnitMismatch);
            return new StanceComparison(ClaimRelation.Indeterminate, reasons,
                left.Magnitude.HasValue != right.Magnitude.HasValue
                    ? "The sources agree on direction but only one reports a magnitude, so they corroborate "
                        + "the finding without agreeing on its size."
                    : "The reported magnitudes use different units, which cannot be compared without a "
                        + "conversion the sources themselves must supply.");
        }

        if (!left.HasInterval || !right.HasInterval)
        {
            reasons.Add(StanceReasonCodes.DirectionalOnly);
            return new StanceComparison(ClaimRelation.Agreement, reasons,
                "Both sources report the same direction; at least one gives no interval, so the agreement is "
                    + "directional only.");
        }

        var overlaps = left.IntervalLow <= right.IntervalHigh && right.IntervalLow <= left.IntervalHigh;
        if (overlaps)
        {
            reasons.Add(StanceReasonCodes.IntervalOverlap);
            return new StanceComparison(ClaimRelation.Agreement, reasons,
                "Both sources report the same direction and their intervals overlap.");
        }

        reasons.Add(StanceReasonCodes.IntervalGap);
        return new StanceComparison(ClaimRelation.Contradiction, reasons,
            "Both sources report the same direction but their intervals do not overlap, so the reported "
                + "magnitudes are in conflict.");
    }

    /// <summary>
    /// True when two reported directions are the same sign of answer, treating the
    /// presence/absence and increase/decrease pairs as opposites.
    /// </summary>
    public static bool AreOpposed(EffectDirection left, EffectDirection right) => (left, right) switch
    {
        (EffectDirection.Increase, EffectDirection.Decrease) => true,
        (EffectDirection.Decrease, EffectDirection.Increase) => true,
        (EffectDirection.Presence, EffectDirection.Absence) => true,
        (EffectDirection.Absence, EffectDirection.Presence) => true,
        (EffectDirection.NoEffect, EffectDirection.Presence) => true,
        (EffectDirection.Presence, EffectDirection.NoEffect) => true,
        (EffectDirection.NoEffect, EffectDirection.Increase) => true,
        (EffectDirection.Increase, EffectDirection.NoEffect) => true,
        (EffectDirection.NoEffect, EffectDirection.Decrease) => true,
        (EffectDirection.Decrease, EffectDirection.NoEffect) => true,
        _ => false,
    };

    /// <summary>
    /// Map a claim's direction onto the stance a matrix cell shows for the
    /// question's hypothesis.
    /// </summary>
    public static SourceStance ToStance(IReadOnlyList<EvidenceClaim> claims)
    {
        if (claims.Count == 0) return SourceStance.NoEvidence;

        var known = claims.Where(c => c.Direction != EffectDirection.Unknown).ToList();
        if (known.Count == 0) return SourceStance.Indeterminate;

        var supports = known.Any(c => c.Direction is EffectDirection.Increase or EffectDirection.Presence);
        var contradicts = known.Any(c => c.Direction is EffectDirection.Decrease or EffectDirection.Absence
            or EffectDirection.NoEffect);

        if (supports && contradicts) return SourceStance.Mixed;
        if (supports) return SourceStance.Supports;
        if (contradicts) return SourceStance.Contradicts;
        return known.Any(c => c.Direction == EffectDirection.Mixed)
            ? SourceStance.Mixed
            : SourceStance.Indeterminate;
    }

    /// <summary>
    /// Normalise a free-text label into the key form used for comparison, so
    /// "Tax Evasion" and "tax  evasion!" collide while "tax evasion" and "tax
    /// compliance" do not.
    /// </summary>
    public static string BuildKey(string? value) => Normalization.BibliographicRecordNormalizer.FoldIdentity(value);

    private static bool KeyEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(BuildKey(left), BuildKey(right), StringComparison.Ordinal);

    private static bool SameUnit(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
