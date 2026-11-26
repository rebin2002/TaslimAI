namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// Direction and language rules for the academic template library. This deliberately mirrors — but does
/// not modify — <c>Taslim.Api.Documents.DocumentTextDirection</c>: the document lane already treats
/// <c>ku</c> and <c>ar</c> as RTL languages, and the template library must agree with that mapping or a
/// Kurdish thesis would preview right-to-left and export left-to-right. Language aliases (ckb/Sorani)
/// normalize here so university presets and the gallery filter chips speak one code.
/// </summary>
public static class AcademicRtlPolicy
{
    /// <summary>Codes the library renders RTL for. "ku" covers Sorani (ckb) in Arabic script, matching the
    /// Document renderer's convention; "ckb" is accepted as an input alias and normalized to "ku".</summary>
    public static readonly IReadOnlyCollection<string> RtlLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ar", "ku" };

    public static string NormalizeLanguage(string? language)
    {
        var code = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code)) return "en";
        if (code is "ckb" or "ku" or "ku-arab" or "ckb-iq") return "ku";
        if (code is "ar" or "ara" || code.StartsWith("ar-", StringComparison.Ordinal)) return "ar";
        if (code is "en" || code.StartsWith("en-", StringComparison.Ordinal)) return "en";
        return "en";
    }

    public static bool IsRtlLanguage(string? language) => RtlLanguages.Contains(NormalizeLanguage(language));

    /// <summary>
    /// A template only ships when its direction and language agree: an RTL language must carry
    /// <c>direction=rtl</c>, and an LTR-designated base may not claim Kurdish/Arabic as its language.
    /// (Mixed-script support lives behind <see cref="AcademicDesignSpec.SupportsRtlAbstractPage"/>,
    /// which keeps the page body direction explicit.)
    /// </summary>
    public static bool IsDirectionConsistent(AcademicDesignSpec spec)
    {
        if (IsRtlLanguage(spec.LanguageCode))
            return string.Equals(spec.Direction, AcademicTextDirection.Rtl, StringComparison.Ordinal)
                || spec.SupportsRtlAbstractPage && string.Equals(spec.Direction, AcademicTextDirection.Ltr, StringComparison.Ordinal)
                && (spec.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase) || spec.LanguageCode.Equals("auto", StringComparison.OrdinalIgnoreCase));
        // A Latin-language base with rtl direction is a layout error unless it is the bilingual frame
        // (English thesis body carrying an RTL abstract page).
        if (string.Equals(spec.Direction, AcademicTextDirection.Rtl, StringComparison.Ordinal))
            return spec.SupportsRtlAbstractPage;
        return true;
    }

    /// <summary>Spine side for print binding: Kurdish/Arabic volumes bind right.</summary>
    public static string RecommendedBindingEdge(AcademicDesignSpec spec) =>
        spec.IsRtl ? "right" : "left";

    /// <summary>
    /// Arabic-script text needs more leading than Latin at the same point size (the joining forms stack
    /// diacritics and loops). The library floors RTL bodies at these values; variant axes may raise,
    /// never lower, them.
    /// </summary>
    public static double MinimumLineSpacingFor(AcademicDesignSpec spec) => spec.IsRtl ? 1.4 : 1.0;

    public static double MinimumBodySizeFor(AcademicDesignSpec spec) => spec.IsRtl ? 12.5 : AcademicAccessibilityPolicy.MinimumBodySizePt;

    /// <summary>CSS font stack for the gallery preview from a spec's body/heading tokens.</summary>
    public static string CssBodyStack(AcademicDesignSpec spec) => AcademicFontTokens.Resolve(spec.BodyFontToken).CssStack;
    public static string CssHeadingStack(AcademicDesignSpec spec) => AcademicFontTokens.Resolve(spec.HeadingFontToken).CssStack;
    public static string CssCaptionStack(AcademicDesignSpec spec) => AcademicFontTokens.Resolve(spec.CaptionFontToken).CssStack;
    public static string CssMonoStack(AcademicDesignSpec spec) => AcademicFontTokens.Resolve(spec.MonoFontToken).CssStack;

    /// <summary>Latin digits → Arabic-Indic (٠١٢٣٤٥٦٧٨٩) for folios and heading numbers when selected.</summary>
    public static string FormatDigits(int value, AcademicDesignSpec spec) =>
        FormatDigits(value.ToString(System.Globalization.CultureInfo.InvariantCulture), spec);

    public static string FormatDigits(string value, AcademicDesignSpec spec)
    {
        if (!string.Equals(spec.DigitSystem, "arabic-indic", StringComparison.Ordinal)) return value;
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
            if (chars[index] is >= '0' and <= '9')
                chars[index] = (char)('\u0660' + (chars[index] - '0'));
        return new string(chars);
    }

    /// <summary>OpenXML page-number format token for a numbering scheme + digit system.</summary>
    public static string WordPageNumberFormat(string scheme, AcademicDesignSpec spec)
    {
        var arabicIndic = string.Equals(spec.DigitSystem, "arabic-indic", StringComparison.Ordinal);
        return scheme switch
        {
            "roman-lower" => "lowerRoman",
            "roman-upper" => "upperRoman",
            "arabic" => arabicIndic ? "arabicIndic" : "decimal",
            _ => "none",
        };
    }

    /// <summary>Roman numerals for front-matter folios (values are page counts ≤ ~120, well-formed).</summary>
    public static string ToRoman(int number)
    {
        if (number is < 1 or > 3999) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var table = new (int Value, string Numerals)[]
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
            (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
        };
        var builder = new System.Text.StringBuilder();
        foreach (var (value, numerals) in table)
            while (number >= value) { builder.Append(numerals); number -= value; }
        return builder.ToString();
    }

    /// <summary>Renders a front/back folio string honouring scheme and digit system.</summary>
    public static string PageLabel(int number, string scheme, AcademicDesignSpec spec) => scheme switch
    {
        "roman-lower" => FormatDigits(ToRoman(number), spec with { DigitSystem = "latin" }),
        "roman-upper" => FormatDigits(ToRoman(number), spec with { DigitSystem = "latin" }).ToUpperInvariant(),
        "arabic" => FormatDigits(number, spec),
        _ => "",
    };
}
