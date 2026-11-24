using System.Globalization;
using System.Text.RegularExpressions;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>
/// Reads a single publication year out of catalogue strings, which are written
/// by cataloguers rather than machines.
///
/// A catalogue entry such as <c>"[19--?]"</c>, <c>"c1978-1982"</c> or
/// <c>"n.d."</c> does not state one year. Rather than pick a midpoint or the
/// first digits it sees, the parser returns no year and the record cites without
/// one, which is the difference between an honest bibliography and an invented
/// date.
/// </summary>
public static partial class PublicationDateParser
{
    /// <summary>Earliest year the platform accepts, since movable-type printing.</summary>
    public const int OldestPlausibleYear = 1450;

    private static readonly string[] NoDateMarkers =
    [
        "n.d.", "n.d", "nd", "undated", "without date", "s.d.", "s.a.", "[s.l.", "unknown",
    ];

    // Not \b...\b: catalogue strings glue letters straight onto the year
    // ("c2024", "©2024"), and the only thing that makes a digit group a year is
    // that it is exactly four digits long.
    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex FourDigitRegex();

    /// <summary>
    /// Returns the four-digit year a token states, or null when the token is
    /// absent, undated, uncertain, ambiguous or implausible.
    /// </summary>
    /// <param name="token">Catalogue string such as <c>"© 2019"</c> or <c>"c2019-2021"</c>.</param>
    /// <param name="observedAtUtc">
    /// Clock for the plausibility ceiling. A year further in the future than the
    /// next calendar year is treated as a typo, never as a forthcoming work.
    /// </param>
    public static string? ParseYear(string? token, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var value = token.Trim();
        if (IsNoDate(value)) return null;

        // Bracketed, interrogative, floruit and range forms are the catalogue's
        // own way of saying "this date is not settled", and a bare "or" inside a
        // date token means two candidate years were offered.
        if (value.Contains('[', StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || value.Contains("--", StringComparison.Ordinal)
            || value.Contains("between", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("fl.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("fl ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidates = FourDigitRegex().Matches(value)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A range such as "1978-1982" or "2019/2020" states two years, and a
        // truncated "19--" states none. Neither yields a single year.
        if (candidates.Count != 1) return null;

        if (!int.TryParse(candidates[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return null;
        if (!IsPlausible(year, observedAtUtc)) return null;

        return year.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a complete ISO date, returning UTC midnight of that day. Partial
    /// dates return null so a year-only claim is not rendered as a day.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? token, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var value = token.Trim();
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) || parts[0].Length != 4) return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)) return null;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return null;

        if (!IsPlausible(year, observedAtUtc)) return null;
        if (month is < 1 or > 12) return null;
        if (day < 1 || DateTime.DaysInMonth(year, month) < day) return null;

        var parsed = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        if (parsed > observedAtUtc.AddYears(1)) return null;

        return parsed;
    }

    private static bool IsPlausible(int year, DateTimeOffset observedAtUtc)
    {
        if (year < OldestPlausibleYear) return false;

        // The ceiling only applies when a real clock was supplied; the epoch
        // stands for "no clock", which the corroboration pass uses.
        if (observedAtUtc == default || observedAtUtc.Year < OldestPlausibleYear) return true;
        return year <= observedAtUtc.Year + 1;
    }

    private static bool IsNoDate(string value)
    {
        var trimmed = value.Trim('.', ' ', '[', ']');
        foreach (var marker in NoDateMarkers)
        {
            if (string.Equals(trimmed, marker.Trim('.', '[', ']'), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

}
