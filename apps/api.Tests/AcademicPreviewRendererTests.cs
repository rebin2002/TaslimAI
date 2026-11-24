using Taslim.Api.AcademicTemplates;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The gallery preview is generated server-side from the same spec as the DOCX/PDF samples, so its
/// HTML contract is load-bearing: escaped-only output, honest synthetic banner per language, RTL
/// direction attributes, and byte-for-byte determinism per configuration.
/// </summary>
public class AcademicPreviewRendererTests
{
    private static AcademicSampleContent Malicious() => new()
    {
        Banner = "SYNTHETIC",
        Title = "<script>alert(1)</script>",
        Subtitle = "<img src=x onerror=evil()>",
        AuthorLine = "A\"B",
        InstitutionLine = "Institution & Partners <b>bold</b>",
        DateLine = "2026",
        ContentsHeading = "Contents",
        AiDisclosureHeading = "AI use",
        AiDisclosureText = "disclosed",
        AbstractHeading = "Abstract",
        AbstractText = "abstract body",
        Contents = new[] { new AcademicSampleTocEntry("1", "<i>Chapter</i>", "1") },
        Sections = new[] { ("<h1>Heading</h1>", "body text with [1] marker", (string?)null) },
        TableCaption = "Table 1",
        TableRows = new IReadOnlyList<string>[] { new[] { "<td>a</td>", "b" } },
        FigureCaption = "Figure 1",
        QuoteText = "quote",
        QuoteAttribution = "attr",
        BibliographyHeading = "References",
        Bibliography = new[] { new AcademicSampleBibliographyEntry("ref", "text <u>u</u>", AcademicSampleContent.BibliographyMarkerEn) },
        AppendixHeading = "Appendix",
        AppendixText = "appendix",
        FootnoteText = "[1] footnote <em>x</em>",
        PageLabelWord = "Page",
    };

    [Fact]
    public void Output_escapes_every_injected_markup_construct()
    {
        var spec = AcademicTemplateRegistry.GetConfiguration("classic-thesis.times.150.classic.center")!.Spec;
        var html = AcademicPreviewRenderer.Render(spec, Malicious());
        // Raw markup must never survive: every injected tag opens as an escaped entity, not a tag.
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<h1>Heading</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>Chapter</i>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;Chapter&lt;/i&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_is_deterministic_and_versioned()
    {
        var cfg = AcademicTemplateRegistry.GetConfiguration("kurdish-thesis-ckb.naskh.latin.150")!;
        Assert.Equal(AcademicPreviewRenderer.Render(cfg.Spec), AcademicPreviewRenderer.Render(cfg.Spec));
        Assert.Equal("1", AcademicPreviewRenderer.PreviewVersion);
    }

    [Fact]
    public void Rtl_configs_get_dir_lang_and_script_specific_banner()
    {
        var kurdish = AcademicTemplateRegistry.GetConfiguration("kurdish-thesis-ckb.naskh.latin.150")!.Spec;
        var html = AcademicPreviewRenderer.Render(kurdish);
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("lang=\"ku\"", html, StringComparison.Ordinal);
        Assert.Contains("پێشبینینی", html, StringComparison.Ordinal);

        var english = AcademicTemplateRegistry.GetConfiguration("classic-thesis.times.150.classic.center")!.Spec;
        var enHtml = AcademicPreviewRenderer.Render(english);
        Assert.Contains("dir=\"ltr\"", enHtml, StringComparison.Ordinal);
        Assert.Contains("SYNTHETIC LAYOUT PREVIEW", enHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("dir=\"rtl\"", enHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_carries_layout_evidence_for_auditable_design_features()
    {
        var hanging = AcademicTemplateRegistry.GetConfiguration("chicago-humanities-monograph.allsans.150.outside.center")
            ?? AcademicTemplateRegistry.Configurations.First(c => c.TemplateSlug == "chicago-humanities-monograph");
        var html = AcademicPreviewRenderer.Render(hanging.Spec);
        if (hanging.Spec.BibliographyHangingIndent)
            Assert.Contains("text-indent", html, StringComparison.Ordinal);
        var tocless = AcademicTemplateRegistry.Configurations.First(c => !c.Spec.IncludeToc);
        var tocHtml = AcademicPreviewRenderer.Render(tocless.Spec);
        Assert.DoesNotContain("academic-toc", tocHtml, StringComparison.Ordinal);
        var tocFull = AcademicTemplateRegistry.Configurations.First(c => c.Spec.IncludeToc);
        Assert.Contains("academic-toc", AcademicPreviewRenderer.Render(tocFull.Spec), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_configuration_preview_renders_without_private_or_backend_tokens()
    {
        foreach (var cfg in AcademicTemplateRegistry.Configurations)
        {
            var html = AcademicPreviewRenderer.Render(cfg.Spec);
            Assert.Contains("academic-preview", html, StringComparison.Ordinal);
            Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        }
    }
}
