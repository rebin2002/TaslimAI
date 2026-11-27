using System.Globalization;
using System.Text;

namespace Taslim.Api.Documents;

/// <summary>
/// Builds the download file name for a rendered Document Studio export.
/// </summary>
/// <remarks>
/// Sanitation here is deliberately independent of <see cref="Path.GetInvalidFileNameChars()"/>,
/// which returns a different character set on Windows and on Unix. The exporters run inside a Linux
/// container, so relying on that table made the same document title produce different download names
/// per host, and let separators, control characters and Windows-reserved characters reach the browser.
/// The Document Studio web client drops any representation whose file name contains a slash, a
/// backslash or a control character, so an unsanitised name silently removes the download buttons
/// from an otherwise successful job.
/// </remarks>
public static class DocumentFileName
{
    /// <summary>Longest stem kept before the extension, matching the historic exporter limit.</summary>
    public const int MaxStemLength = 120;

    /// <summary>Hard ceiling for the full name, aligned with the client-side download gate.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>Stem used when a title carries no usable characters.</summary>
    public const string FallbackStem = "Taslim document";

    private const char Replacement = '_';

    private static readonly IReadOnlySet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns a stem plus lower-cased extension that is safe on every supported download target and
    /// identical on every host for the same input.
    /// </summary>
    public static string Build(string? title, string? extension)
    {
        var suffix = NormalizeExtension(extension);
        var allowedStemLength = Math.Min(MaxStemLength, Math.Max(1, MaxFileNameLength - suffix.Length));
        var stem = TrimEdges(TruncateOnCharacterBoundary(SanitizeStem(title), allowedStemLength));
        // A stem of nothing but replacements is noise, not a name: a title of "///" must not
        // download as "_", so it falls back like an empty title does.
        if (stem.Length == 0 || stem.All(character => character == Replacement))
        {
            stem = FallbackStem;
        }
        else if (ReservedDeviceNames.Contains(stem))
        {
            // "CON.docx" is unwritable on Windows, so keep it visible but inert.
            stem = Replacement + stem;
        }

        return stem + suffix;
    }

    private static string SanitizeStem(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        for (var index = 0; index < title.Length; index++)
        {
            var character = title[index];
            if (char.IsHighSurrogate(character))
            {
                // Keep a well-formed supplementary character intact. A stray surrogate would be
                // re-encoded as U+FFFD by JSON or header serialisation, so it is replaced instead.
                if (index + 1 < title.Length && char.IsLowSurrogate(title[index + 1]))
                {
                    builder.Append(character).Append(title[index + 1]);
                    index++;
                }
                else
                {
                    builder.Append(Replacement);
                }

                continue;
            }

            builder.Append(char.IsLowSurrogate(character) || IsForbidden(character) ? Replacement : character);
        }

        return Collapse(builder.ToString());
    }

    private static bool IsForbidden(char character) => character switch
    {
        // Path separators plus characters that are ambiguous once a download manager resolves them.
        '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => true,
        // Any C0/C1 control character, including tab, newline and DEL.
        _ when char.IsControl(character) => true,
        // U+2028 and U+2028-style separators break line-based header handling.
        _ when character is '\u2028' or '\u2029' => true,
        // Formatting and invisible characters can spoof an extension or a different base name.
        _ when char.GetUnicodeCategory(character) == UnicodeCategory.Format => true,
        // Non-characters do not survive UTF-8 encoding.
        '\uFFFE' or '\uFFFF' => true,
        _ => false,
    };

    /// <summary>
    /// Collapses runs of replacement characters and repeated dots so traversal fragments such as
    /// ".." or "..." cannot survive, and a long run of replacements does not read as noise.
    /// </summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var previous = builder.Length > 0 ? builder[^1] : '\0';
            if ((character == Replacement || character == '.') && character == previous)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes edge characters that make a name unusable: a leading dot hides the file or reads as
    /// a traversal segment, and Windows rejects names ending in a dot or a space.
    /// </summary>
    private static string TrimEdges(string value)
    {
        var trimmed = value;
        while (trimmed.Length > 0)
        {
            var next = trimmed.Trim();
            next = next.TrimStart('.');
            next = next.TrimEnd('.', ' ');
            if (next.Length == trimmed.Length)
            {
                return next;
            }

            trimmed = next;
        }

        return string.Empty;
    }

    private static string TruncateOnCharacterBoundary(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Never cut a supplementary character in half: a lone surrogate corrupts the download name.
        var length = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..Math.Max(0, length)];
    }

    private static string NormalizeExtension(string? extension)
    {
        var candidate = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate[0] != '.')
        {
            candidate = "." + candidate;
        }

        if (candidate.Length > 16)
        {
            candidate = candidate[..16];
        }

        var builder = new StringBuilder(candidate.Length);
        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];
            // The leading dot marks the extension; anything else unsafe becomes a replacement.
            builder.Append(index == 0 || !IsForbidden(character) && character != '.' ? character : Replacement);
        }

        return builder.ToString();
    }
}
