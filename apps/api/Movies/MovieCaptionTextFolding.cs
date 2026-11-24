namespace Taslim.Api.Movies;

/// <summary>
/// Both supported caption containers end a cue at the first blank line, so
/// authored cue text can never carry one. The exporters used to write stored
/// text verbatim, which turned a single stored cue into several blocks on
/// download: the reader then either rejected the file ("Each SRT cue must
/// contain a start and end timecode") or kept only the text before the blank
/// line, because the WebVTT reader stops collecting at the first blank line.
/// A download must therefore fold blank lines away so the document stays
/// inside the grammar the importer enforces.
/// </summary>
internal static class MovieCaptionTextFolding
{
    /// <summary>
    /// Returns <paramref name="text"/> with its line endings normalised to
    /// <c>\n</c> and every empty or whitespace-only line removed. Non-blank
    /// lines keep their own content untouched, so genuine multi-line cues
    /// survive exactly as authored.
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0) return text;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return string.Join('\n', lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }
}
