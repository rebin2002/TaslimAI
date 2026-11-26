import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "./LocaleProvider";
import { StudioChooser, productAdTileCopy, productAdTileHref } from "./StudioChooser";
import { locales } from "@/lib/i18n";

describe("Product Ad Create/Home entry point", () => {
  it("exposes one truthful link in both Create and compact Home chooser", () => {
    const full = renderToStaticMarkup(<LocaleProvider><StudioChooser /></LocaleProvider>);
    const compact = renderToStaticMarkup(<LocaleProvider><StudioChooser compact /></LocaleProvider>);
    expect(productAdTileHref).toBe("/create/product-ad");
    for (const markup of [full, compact]) {
      expect((markup.match(/data-product-ad-tile="true"/g) ?? [])).toHaveLength(1);
      expect(markup).toContain('href="/create/product-ad"');
      expect(markup).not.toContain("Generate Product Ad with AI");
      expect(markup).not.toContain("Publish product ad");
    }
    const displayed = Number(/class="studio-chooser-count">(\d+)</.exec(full)?.[1] ?? -1);
    expect(displayed).toBe((full.match(/class="studio-card"/g) ?? []).length);
  });

  it("uses bounded feature-local copy in EN/AR/Sorani without claiming paid generation", () => {
    for (const locale of locales) {
      const copy = productAdTileCopy(locale);
      expect(copy.category.trim()).not.toBe("");
      expect(copy.title.trim()).not.toBe("");
      expect(copy.hint.trim()).not.toBe("");
      expect(copy.hint.toLowerCase()).not.toMatch(/paid|publish|provider|payment/);
    }
    expect(productAdTileCopy("ar").title).toContain("إعلان");
    expect(productAdTileCopy("ku").title).toContain("ڕیکلام");
  });
});
