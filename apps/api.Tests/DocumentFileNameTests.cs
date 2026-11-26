using System.Text.RegularExpressions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Covers the deterministic, path-safe encoding of Document Studio download names. The expectations
/// are literal on purpose: they must hold on a Windows, macOS and Linux host alike, and they mirror
/// the acceptance gate in apps/web/src/lib/documentStudioState.ts, which drops any representation
/// whose file name carries a separator or a control character and therefore hides the download
/// buttons of an otherwise successful job.
/// </summary>
public sealed class DocumentFileNameTests
{
    private static readonly Regex DownloadGateForbidden = new(@"[\\/\u0000-\u001f\u007f]", RegexOptions.Compiled);

    [Theory]
    [InlineData("Launch brief", ".docx", "Launch brief.docx")]
    [InlineData("  Quarterly report  ", ".pdf", "Quarterly report.pdf")]
    [InlineData("Raporê Derbarê", ".md", "Raporê Derbarê.md")]
    [InlineData("کورتەی هەفتانە", ".docx", "کورتەی هەفتانە.docx")]
    [InlineData("Ship 🎉 day", ".pdf", "Ship 🎉 day.pdf")]
    public void Build_keeps_usable_titles_verbatim(string title, string extension, string expected)
    {
        Assert.Equal(expected, DocumentFileName.Build(title, extension));
    }

    [Theory]
    // Characters that Path.GetInvalidFileNameChars() only reports on Windows.
    [InlineData("A:B*C?D\"E|F<G>H", "A_B_C_D_E_F_G_H")]
    // A backslash is a separator on Windows and survives untouched on a Linux exporter host.
    [InlineData("Rapor\\Basy", "Rapor_Basy")]
    // Line breaks inside a provider-influenced title must not reach the browser.
    [InlineData("Q3 Report\nSummary", "Q3 Report_Summary")]
    [InlineData("Q3 Report\r\tSummary", "Q3 Report_Summary")]
    [InlineData("Budget\u0007draft", "Budget_draft")]
    [InlineData("Budget\u007fdraft", "Budget_draft")]
    // Bidi and zero-width overrides spoof a base name or an extension.
    [InlineData("report\u202Egnp", "report_gnp")]
    [InlineData("brief\u200Bfinal", "brief_final")]
    [InlineData("nonchar\uFFFF", "nonchar_")]
    public void Build_replaces_every_unsafe_character_without_the_host_table(string title, string expectedStem)
    {
        Assert.Equal(expectedStem + ".pdf", DocumentFileName.Build(title, ".pdf"));
    }

    [Fact]
    public void Build_replaces_lone_surrogates_that_json_and_headers_would_otherwise_corrupt()
    {
        // Built in code: an InlineData argument is serialised through discovery and loses the
        // unpaired surrogate before the product code ever sees it.
        Assert.Equal("surrogate_tail.pdf", DocumentFileName.Build("surrogate" + (char)0xD800 + "tail", ".pdf"));
        Assert.Equal("tail_.pdf", DocumentFileName.Build("tail" + (char)0xDC00, ".pdf"));
        // A well-formed pair stays intact.
        Assert.Equal("a\U0001F389b.pdf", DocumentFileName.Build("a\U0001F389b", ".pdf"));
    }

    [Theory]
    [InlineData("..\\..\\windows\\system32\\config")]
    [InlineData("../../etc/passwd")]
    [InlineData("....")]
    [InlineData("report...final")]
    [InlineData("a//b")]
    public void Build_never_emits_a_traversal_segment(string title)
    {
        var name = DocumentFileName.Build(title, ".docx");
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain("/", name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
        Assert.False(Path.IsPathRooted(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("")]
    [InlineData(null)]
    public void Build_falls_back_when_nothing_usable_remains(string? title)
    {
        Assert.Equal($"{DocumentFileName.FallbackStem}.docx", DocumentFileName.Build(title, ".docx"));
    }

    [Theory]
    [InlineData(".env", "env")]
    [InlineData(" .hidden", "hidden")]
    [InlineData("report.", "report")]
    [InlineData("report. ", "report")]
    [InlineData(" trailing", "trailing")]
    public void Build_strips_dots_and_spaces_from_both_ends(string title, string expectedStem)
    {
        Assert.Equal(expectedStem + ".pdf", DocumentFileName.Build(title, ".pdf"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void Build_inertizes_windows_reserved_device_names(string title)
    {
        var name = DocumentFileName.Build(title, ".docx");
        Assert.Equal($"_{title}.docx", name);
        Assert.NotEqual($"{title}.docx", name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_truncates_to_the_stem_limit_without_splitting_a_surrogate_pair()
    {
        var title = new string('a', DocumentFileName.MaxStemLength - 1) + "🎉 tail";
        var name = DocumentFileName.Build(title, ".docx");
        var stem = name[..^".docx".Length];
        Assert.Equal(DocumentFileName.MaxStemLength - 1, stem.Length);
        Assert.EndsWith("a", stem, StringComparison.Ordinal);
        AssertNoLoneSurrogates(name);
    }

    [Fact]
    public void Build_keeps_a_supplementary_character_that_fits_inside_the_limit()
    {
        var title = new string('b', DocumentFileName.MaxStemLength - 2) + "🎉";
        var name = DocumentFileName.Build(title, ".pdf");
        var stem = name[..^".pdf".Length];
        Assert.Equal(DocumentFileName.MaxStemLength, stem.Length);
        Assert.EndsWith("🎉", stem, StringComparison.Ordinal);
        AssertNoLoneSurrogates(name);
    }

    [Fact]
    public void Build_bounds_the_full_name_length()
    {
        var name = DocumentFileName.Build(new string('c', 5_000), ".docx");
        Assert.True(name.Length <= DocumentFileName.MaxFileNameLength);
        Assert.EndsWith(".docx", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".PDF", "brief.pdf")]
    [InlineData(".docx", "brief.docx")]
    [InlineData("pdf", "brief.pdf")]
    [InlineData("", "brief")]
    [InlineData(null, "brief")]
    public void Build_normalizes_the_extension_only(string? extension, string expected)
    {
        Assert.Equal(expected, DocumentFileName.Build("brief", extension));
    }

    [Fact]
    public void Build_is_deterministic_for_repeated_calls()
    {
        const string title = "Q3: Report/Summary *final*";
        Assert.Equal(DocumentFileName.Build(title, ".docx"), DocumentFileName.Build(title, ".docx"));
        Assert.Equal("Q3_ Report_Summary _final_.docx", DocumentFileName.Build(title, ".docx"));
    }

    [Theory]
    [InlineData("Q3 Report\nSummary")]
    [InlineData("Rapor\\Basy: Q3")]
    [InlineData("Budget \"Final\" <2026>")]
    [InlineData("..\\" + "..\\" + "passwd")]
    [InlineData("CON")]
    [InlineData("...")]
    [InlineData("Ship \uD83C\uDF89 day")]
    [InlineData("کورتەی هەفتانە")]
    [InlineData("")]
    public void Rendered_document_names_pass_the_browser_download_gate(string title)
    {
        var docx = Render(title, AssetRepresentationTypes.Docx);
        var pdf = Render(title, AssetRepresentationTypes.Pdf);
        AssertRepresentationAccepted(docx, "docx");
        AssertRepresentationAccepted(pdf, "pdf");
        // The exporter integrity gate keys off the same suffix and must keep accepting the output.
        DocumentOutputIntegrityValidator.Validate(docx);
        DocumentOutputIntegrityValidator.Validate(pdf);
    }

    [Fact]
    public void Rendered_document_names_match_the_exporter_suffix_for_plain_titles()
    {
        Assert.Equal("Launch brief.docx", Render("Launch brief", AssetRepresentationTypes.Docx).FileName);
        Assert.Equal("Launch brief.pdf", Render("Launch brief", AssetRepresentationTypes.Pdf).FileName);
    }

    /// <summary>Mirrors isSafeDocumentRepresentation() in apps/web/src/lib/documentStudioState.ts.</summary>
    private static void AssertRepresentationAccepted(RenderedDocument rendered, string type)
    {
        var fileName = rendered.FileName;
        Assert.False(DownloadGateForbidden.IsMatch(fileName), $"the download gate would drop '{fileName}'");
        Assert.True(fileName.Length > type.Length + 1, $"'{fileName}' is too short for '.{type}'");
        Assert.True(fileName.Length <= DocumentFileName.MaxFileNameLength, $"'{fileName}' exceeds the gate limit");
        Assert.EndsWith($".{type}", fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoLoneSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Assert.True(index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]), $"lone high surrogate at {index}");
                index++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(value[index]), $"lone low surrogate at {index}");
        }
    }

    private static RenderedDocument Render(string title, string representationType)
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, title, "Create a brief.", "report", "short", null, null, [], "en", "both", "professional", false);
        var draft = new DocumentDraft
        {
            Title = "Launch brief",
            Summary = "A short brief.",
            Sections = [new DocumentSection
            {
                Heading = "Overview",
                Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "The launch is ready for review." }],
            }],
        };
        var renderer = new DocumentRenderer();
        return representationType == AssetRepresentationTypes.Docx
            ? renderer.RenderDocx(draft, input, new DocumentGenerationOptions())
            : renderer.RenderPdf(draft, input, new DocumentGenerationOptions());
    }
}
