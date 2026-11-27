import { describe, expect, it } from "vitest";
import { academicCopy, academicCopyLocales, missingCopyKeys } from "./academicTemplateCopy";
import { sanitizeFontStack, typographyFor } from "./academicTemplateFonts";
import { bundledCatalog as catalog, getFontToken } from "./academicTemplateCatalog";
import { getConfiguration } from "./academicTemplateCatalog";

describe("academic gallery copy", () => {
  it("keeps kurdish and arabic locales complete against english", () => {
    for (const locale of academicCopyLocales) {
      expect(missingCopyKeys(locale), locale).toEqual([]);
    }
  });

  it("interpolates counts and names without leaving placeholders", () => {
    expect(academicCopy("en", "resultsCount", { count: 230 })).toBe("230 distinct designs");
    expect(academicCopy("ar", "resultsCount", { count: 76 })).toContain("76");
    const rendered = academicCopy("ckb", "previewAria", { name: "Classic Thesis" });
    expect(rendered).toContain("Classic Thesis");
    expect(rendered).not.toContain("{name}");
  });

  it("uses rtl-friendly text for rtl locales (no left-to-right-only wording)", () => {
    for (const locale of ["ckb", "ar"] as const) {
      const integrity = academicCopy(locale, "integrityNote");
      // Kurdish/Arabic scripts occupy the Arabic unicode blocks.
      expect(integrity).toMatch(/[\u0600-\u06FF]/);
    }
  });
});

describe("academic font token resolution", () => {
  it("sanitises quotes so a font stack can never break out of an attribute", () => {
    const evil = 'x"; background:url(https://evil/;y';
    const safe = sanitizeFontStack(evil);
    expect(safe).not.toContain('"');
    expect(safe.startsWith("x';")).toBe(true);
    // Real catalog stacks all survive sanitisation with family names intact.
    for (const token of catalog.fontTokens) {
      const stack = sanitizeFontStack(token.cssStack);
      expect(stack).toContain(token.docxFamily.split(",")[0].trim().split(" ")[0]);
    }
  });

  it("resolves a css stack for every token the 230 configurations reference", () => {
    const tokens = new Set<string>();
    for (const configuration of catalog.configurations) {
      tokens.add(configuration.spec.bodyFontToken);
      tokens.add(configuration.spec.headingFontToken);
      tokens.add(configuration.spec.captionFontToken);
      tokens.add(configuration.spec.monoFontToken);
    }
    for (const token of tokens) {
      expect(getFontToken(catalog, token), token).toBeDefined();
    }
  });

  it("derives heading sizes from the modular type scale", () => {
    const configuration = getConfiguration(catalog, "classic-thesis.times.150.classic.center")!;
    const typography = typographyFor(catalog, configuration.spec);
    expect(typography.bodySizePt).toBe(12);
    const h1 = typography.headingSizePt(1);
    const h2 = typography.headingSizePt(2);
    expect(h1).toBeGreaterThan(h2);
    expect(Math.round(h1 / configuration.spec.bodySizePt * 100) / 100).toBeCloseTo(Math.pow(configuration.spec.typeScaleRatio, 3), 1);
    expect(typography.arabicScript).toBe(false);
    const kurdish = typographyFor(catalog, getConfiguration(catalog, "kurdish-thesis-ckb.naskh.latin.150")!.spec);
    expect(kurdish.arabicScript).toBe(true);
  });
});
