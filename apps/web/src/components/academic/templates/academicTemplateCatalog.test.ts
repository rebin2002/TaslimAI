import { describe, expect, it } from "vitest";
import {
  bundledCatalog,
  configurationCountsByTemplate,
  countConfigurations,
  distinctDesignHashes,
  filterConfigurations,
  getConfiguration,
  normalizeAcademicLanguage,
  syntheticBannerFor,
} from "./academicTemplateCatalog";

describe("bundled academic template catalog", () => {
  it("carries a vetted 15–25 base set with 200+ genuinely distinct configurations", () => {
    expect(bundledCatalog.baseTemplateCount).toBeGreaterThanOrEqual(15);
    expect(bundledCatalog.baseTemplateCount).toBeLessThanOrEqual(25);
    expect(bundledCatalog.templates).toHaveLength(bundledCatalog.baseTemplateCount);
    expect(bundledCatalog.configurations).toHaveLength(bundledCatalog.distinctConfigurationCount);
    expect(bundledCatalog.distinctConfigurationCount).toBeGreaterThanOrEqual(200);
    // No aliases: every design hash is unique across the whole library.
    expect(distinctDesignHashes(bundledCatalog)).toHaveLength(bundledCatalog.configurations.length);
    const ids = new Set(bundledCatalog.configurations.map((entry) => entry.configurationId));
    expect(ids.size).toBe(bundledCatalog.configurations.length);
  });

  it("gives every base template at least six selectable variants", () => {
    const counts = configurationCountsByTemplate(bundledCatalog);
    for (const template of bundledCatalog.templates) {
      expect(counts[template.slug], template.slug).toBeGreaterThanOrEqual(6);
    }
  });

  it("covers thesis/dissertation/proposal/report kinds with rtl kurdish and arabic lineages", () => {
    const slugs = bundledCatalog.templates.map((template) => template.slug);
    expect(slugs.filter((slug) => slug.includes("kurdish") || slug.includes("ckb")).length).toBeGreaterThanOrEqual(2);
    expect(slugs.filter((slug) => slug.includes("arabic")).length).toBeGreaterThanOrEqual(2);
    const rtlCount = filterConfigurations(bundledCatalog, { direction: "rtl" }).length;
    expect(rtlCount).toBeGreaterThanOrEqual(50);
    expect(bundledCatalog.languages).toEqual(expect.arrayContaining(["en", "ar", "ku"]));
  });

  it("normalises gallery language aliases exactly like the API", () => {
    expect(normalizeAcademicLanguage("ckb")).toBe("ku");
    expect(normalizeAcademicLanguage("ckb-IQ")).toBe("ku");
    expect(normalizeAcademicLanguage("ar-EG")).toBe("ar");
    expect(normalizeAcademicLanguage("en-GB")).toBe("en");
    expect(normalizeAcademicLanguage("fr")).toBe("en");
    const viaAlias = filterConfigurations(bundledCatalog, { language: "ckb" });
    const canonical = filterConfigurations(bundledCatalog, { language: "ku" });
    expect(viaAlias.map((entry) => entry.configurationId)).toEqual(canonical.map((entry) => entry.configurationId));
    expect(viaAlias.length).toBeGreaterThanOrEqual(20);
  });

  it("filters by document kind, citation style, paper, direction and free text like the facets route", () => {
    expect(countConfigurations(bundledCatalog, { documentKind: "dissertation" })).toBeGreaterThanOrEqual(30);
    expect(countConfigurations(bundledCatalog, { citationStyle: "ieee" })).toBeGreaterThanOrEqual(6);
    expect(countConfigurations(bundledCatalog, { paperFormat: "letter" })).toBeGreaterThanOrEqual(20);
    const twoColumn = filterConfigurations(bundledCatalog, { query: "double-column" });
    expect(twoColumn.length).toBeGreaterThanOrEqual(6);
    expect(twoColumn.every((entry) => entry.spec.columnCount === 2)).toBe(true);
    expect(countConfigurations(bundledCatalog, {})).toBe(bundledCatalog.distinctConfigurationCount);
  });

  it("points every university preset at a real configuration", () => {
    expect(bundledCatalog.presets.length).toBeGreaterThanOrEqual(4);
    for (const preset of bundledCatalog.presets) {
      const target = getConfiguration(bundledCatalog, preset.defaultConfigurationId);
      expect(target, preset.id).toBeDefined();
    }
  });

  it("ships localized synthetic banners that flag placeholder-only previews", () => {
    expect(bundledCatalog.syntheticBanners).toHaveLength(3);
    expect(syntheticBannerFor(bundledCatalog, "en")).toContain("SYNTHETIC LAYOUT PREVIEW");
    expect(syntheticBannerFor(bundledCatalog, "ckb")).toContain("پێشبینینی ڕووکاری نمونەیی");
    expect(syntheticBannerFor(bundledCatalog, "ar-EG")).toContain("معاينة تنسيق تجريبية");
    for (const banner of bundledCatalog.syntheticBanners) {
      expect(banner.length).toBeGreaterThan(40);
    }
  });

  it("contains no provider, model, routing or cost vocabulary anywhere in the snapshot", () => {
    const serialized = JSON.stringify(bundledCatalog);
    const forbidden =
      /\b(openai|anthropic|claude|gpt-\d*|gemini|azure|bedrock|vertex|sora|elevenlabs|whisper|api[_-]?key|bearer|billing|pricing|price|cost|credits|inference|router|routing|model)\b/i;
    const offenders = serialized.match(new RegExp(forbidden.source, "gi")) ?? [];
    expect([...new Set(offenders)]).toEqual([]);
  });

  it("keeps preview specs free of fabricated bibliographic locators", () => {
    // Design data may legitimately carry hex colours and pt sizes — but no URLs/DOIs.
    const serialized = JSON.stringify(bundledCatalog.templates.map((template) => template.description));
    expect(serialized).not.toMatch(/https?:\/\//);
    expect(serialized).not.toMatch(/\bdoi\b/i);
  });
});
