import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { bundledCatalog as catalog } from "./academicTemplateCatalog";
import { AcademicTemplateGallery } from "./AcademicTemplateGallery";
import { AcademicTemplateGalleryContainer } from "./AcademicTemplateGalleryContainer";
import { AcademicTemplatePreviewCard } from "./AcademicTemplatePreviewCard";
import { getConfiguration } from "./academicTemplateCatalog";

describe("AcademicTemplateGallery", () => {
  it("opens with the full library count and the first page of cards only", () => {
    const html = renderToStaticMarkup(<AcademicTemplateGallery catalog={catalog} />);
    expect(html).toContain(`${catalog.distinctConfigurationCount} distinct designs`);
    expect(html).toContain('role="status"');
    expect(html).toContain('aria-live="polite"');
    const cardCount = (html.match(/class="academic-template-card"/g) ?? []).length;
    expect(cardCount).toBe(24);
    expect(cardCount).toBeLessThan(catalog.distinctConfigurationCount);
    expect(html).toContain("Show more designs");
  });

  it("exposes every facet from the taxonomy plus the friendly ckb alias option", () => {
    const html = renderToStaticMarkup(<AcademicTemplateGallery catalog={catalog} />);
    for (const kind of catalog.documentKinds) expect(html).toContain(`<option value="${kind}">${kind}</option>`);
    for (const style of catalog.citationStyles) expect(html).toContain(`>${style}<`);
    expect(html).toContain('<option value="ckb">ckb</option>');
    expect(html).toContain("Right-to-left");
    expect(html).toContain("Search designs");
  });

  it("states integrity, zero-charge snapshot and privacy guarantees under every render", () => {
    const html = renderToStaticMarkup(<AcademicTemplateGallery catalog={catalog} />);
    expect(html).toContain("They never hide authorship.");
    expect(html).toContain("does not trigger a new content generation charge.");
    expect(html).toContain("No provider, model or cost information is shown.");
    expect(html).toContain(`${catalog.baseTemplateCount} base templates · ${catalog.distinctConfigurationCount} configurations`);
  });

  it("renders the gallery right-to-left for kurdish and arabic locales with complete copy", () => {
    for (const locale of ["ckb", "ar"] as const) {
      const html = renderToStaticMarkup(<AcademicTemplateGallery catalog={catalog} locale={locale} />);
      expect(html).toContain('dir="rtl"');
      expect(html).not.toContain("undefined");
      expect(html).not.toContain("{count}");
      expect(html).toContain(`${catalog.distinctConfigurationCount} `);
    }
  });

  it("never leaks provider, model or cost vocabulary through card metadata", () => {
    const html = renderToStaticMarkup(<AcademicTemplateGallery catalog={catalog} />);
    expect(html.toLowerCase()).not.toMatch(/\b(openai|anthropic|claude|gemini|azure|billing|pricing|inference|api key)\b/);
  });

  it("renders a selectable card with variants, fingerprint and accessible preview label", () => {
    const configuration = getConfiguration(catalog, "classic-thesis.times.150.classic.center")!;
    const html = renderToStaticMarkup(
      <AcademicTemplatePreviewCard catalog={catalog} configuration={configuration} selected onSelect={() => undefined} />,
    );
    expect(html).toContain('aria-pressed="true"');
    expect(html).toContain('data-design-hash="');
    expect(html).toContain("Selected");
    expect(html).toContain("academic-variant-chip");
    expect(html).toContain("Design fingerprint");
  });

  it("container falls back to the bundled snapshot while loading without any network", () => {
    const html = renderToStaticMarkup(<AcademicTemplateGalleryContainer />);
    expect(html).toContain('data-academic-catalog-source="loading"');
    expect(html).toContain(`${catalog.distinctConfigurationCount} distinct designs`);
  });
});
