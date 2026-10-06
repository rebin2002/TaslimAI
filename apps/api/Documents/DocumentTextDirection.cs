using Taslim.Api.Contracts;

namespace Taslim.Api.Documents;

internal static class DocumentTextDirection
{
    public static bool IsRtl(string? language, DocumentDraft draft)
    {
        if (string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "ku", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) return false;

        var rtlLetters = 0;
        var ltrLetters = 0;
        CountLetters(draft.Title, ref rtlLetters, ref ltrLetters);
        CountLetters(draft.Summary, ref rtlLetters, ref ltrLetters);
        foreach (var section in draft.Sections)
        {
            CountLetters(section.Heading, ref rtlLetters, ref ltrLetters);
            foreach (var block in section.Blocks)
            {
                CountLetters(block.Text, ref rtlLetters, ref ltrLetters);
                if (block.Items is not null)
                    foreach (var item in block.Items) CountLetters(item, ref rtlLetters, ref ltrLetters);
                if (block.Rows is not null)
                    foreach (var row in block.Rows)
                        foreach (var cell in row.Cells) CountLetters(cell, ref rtlLetters, ref ltrLetters);
            }
        }
        return rtlLetters > ltrLetters;
    }

    private static void CountLetters(string? value, ref int rtlLetters, ref int ltrLetters)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var character in value)
        {
            if (IsRtlCharacter(character)) rtlLetters++;
            else if (char.IsLetter(character)) ltrLetters++;
        }
    }

    private static bool IsRtlCharacter(char character) =>
        character is >= '\u0590' and <= '\u05FF' // Hebrew
            or >= '\u0600' and <= '\u06FF' // Arabic
            or >= '\u0700' and <= '\u074F' // Syriac
            or >= '\u0750' and <= '\u077F' // Arabic Supplement
            or >= '\u0780' and <= '\u07BF' // Thaana
            or >= '\u07C0' and <= '\u07FF' // NKo
            or >= '\u08A0' and <= '\u08FF' // Arabic Extended-A
            or >= '\uFB50' and <= '\uFDFF' // Arabic Presentation Forms-A
            or >= '\uFE70' and <= '\uFEFF'; // Arabic Presentation Forms-B
}
