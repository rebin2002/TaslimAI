import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { getConfiguration, normalizeAcademicLanguage, syntheticBannerFor } from "./academicTemplateCatalog";
import { AcademicTemplatePreview, pageLabel, toRomanLower } from "./AcademicTemplatePreview";
import type { AcademicDesignSpec } from "./academicTemplateTypes";
import { bundledCatalog as catalog } from "./academicTemplateCatalog";

function specOf(configurationId: string): AcademicDesignSpec {
  const configuration = getConfiguration(catalog, configurationId);
  if (!configuration) throw new Error(`missing configuration ${configurationId}`);
  return configuration.spec;
}

describe("pageLabel mirror of the server pagination policy", () => {
  it("renders lowercase romans, uppercase romans, decimals and blanks", () => {
    expect(toRomanLower(14)).toBe("xiv");
    expect(pageLabel(14, "roman-lower", { digitSystem: "latin" })).toBe("xiv");
    expect(pageLabel(3, "roman-upper", { digitSystem: "latin" })).toBe("III");
    expect(pageLabel(9, "arabic", { digitSystem: "latin" })).toBe("9");
    expect(pageLabel(4, "none", { digitSystem: "latin" })).toBe("");
  });

  it("converts digits to arabic-indic when the design spec says so", () => {
    expect(pageLabel(12, "arabic", { digitSystem: "arabic-indic" })).toBe("١٢");
    // Roman symbols are untouched by the digit system, exactly like the API helper.
    expect(pageLabel(14, "roman-lower", { digitSystem: "arabic-indic" })).toBe("xiv");
  });
});

describe("AcademicTemplatePreview", () => {
  it("renders the classic thesis lineage with contents, roman front matter and dotted leaders", () => {
    const spec = specOf("classic-thesis.times.150.classic.center");
    const html = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={spec} configurationId="classic-thesis.times.150.classic.center" />);
    expect(html).toContain('dir="ltr"');
    expect(html).toContain('lang="en"');
    expect(html).toContain("SYNTHETIC LAYOUT PREVIEW");
    expect(html).toContain("academic-toc");
    expect(html).toContain("academic-bibliography");
    expect(html).toContain("Statement of Generative-AI Use");
    expect(html).toContain(">iv<"); // roman-lower front matter folio
  });

  it("flips to right-to-left with localized banners for kurdish and arabic designs", () => {
    const kurdish = specOf("kurdish-thesis-ckb.naskh.latin.150");
    const kuHtml = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={kurdish} />);
    expect(kuHtml).toContain('dir="rtl"');
    expect(kuHtml).toContain('lang="ku"');
    expect(kuHtml).toContain(syntheticBannerFor(catalog, "ku"));
    expect(kuHtml).toContain("پێشبینینی ڕووکاری نمونەیی");

    const arabic = specOf("arabic-dissertation-ar.naskh.band.indic.outside");
    const arHtml = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={arabic} />);
    expect(arHtml).toContain('dir="rtl"');
    expect(arHtml).toContain('lang="ar"');
    expect(arHtml).toContain("٣"); // arabic-indic body folio
    expect(arHtml).toContain("معاينة تنسيق تجريبية");
    expect(arHtml).toContain('text-align:end'); // outside-spine folio anchor
  });

  it("doubles the column count for two-column journal designs and never for theses", () => {
    const journal = specOf("journal-double-column.times.numeric.narrow");
    const html = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={journal} configurationId="journal-double-column.times.numeric.narrow" />);
    expect(html).toContain("column-count:2");
    expect(html).toContain("[1]"); // numeric in-text marker style
    const thesis = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={specOf("classic-thesis.times.150.classic.center")} />);
    expect(thesis).not.toContain("column-count");
  });

  it("honours heading-numbering and TOC-depth choices", () => {
    const spec = specOf("modern-dissertation.palatinocalibri.150.twopart.title");
    const unnumbered: AcademicDesignSpec = { ...spec, headingNumbering: "none", tocDepth: 1 };
    const html = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={unnumbered} />);
    expect(html).toContain("Introduction to the Layout");
    expect(html).not.toMatch(/>1\. Introduction</);
    expect(html).not.toContain("Problem Statement"); // depth-1 contents hides the sub-entry
  });

  it("escapes injected markup instead of interpreting it (no arbitrary HTML ingestion)", () => {
    const spec = specOf("classic-thesis.times.150.classic.center");
    const html = renderToStaticMarkup(
      <AcademicTemplatePreview
        catalog={catalog}
        spec={spec}
        sample={{
          title: '<img src=x onerror="steal()" data-probe="yes">',
          abstractText: "<script>alert('xss')</script>",
          bibliography: [{ label: "evil", text: '<svg onload="boom()">keep</svg>' }],
        }}
      />,
    );
    expect(html).not.toContain("<img src=x");
    expect(html).not.toContain("<script>alert");
    expect(html).not.toContain("<svg onload");
    expect(html).toContain("&lt;img");
    expect(html).toContain("&lt;script&gt;");
  });

  it("never fabricates sources: previews carry no resolvable locators and say they are samples", () => {
    for (const configuration of catalog.configurations.slice(0, 40)) {
      const html = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={configuration.spec} />);
      expect(html).not.toMatch(/https?:\/\//);
      expect(html).not.toMatch(/\bdoi\b/i);
      expect(html.toLowerCase()).toContain(normalizeAcademicLanguage(configuration.spec.languageCode) === "en" ? "not a real source" : "sample");
    }
  });

  it("renders deterministic markup for a given spec", () => {
    const spec = specOf("bilingual-abstract-thesis.kurdish.times.150.classic");
    const first = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={spec} />);
    const second = renderToStaticMarkup(<AcademicTemplatePreview catalog={catalog} spec={spec} />);
    expect(first).toBe(second);
    expect(first).toContain("academic-abstract-secondary"); // separate english abstract slot
  });
});
