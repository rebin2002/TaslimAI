import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { StudioChooser, healthCareTileHref } from "./StudioChooser";
import { LocaleProvider } from "./LocaleProvider";
import { healthCareCopy, healthCareDirection, healthCareLanguageTag } from "@/lib/healthCareI18n";
import { locales } from "@/lib/i18n";

const renderFull = () => decode(renderToStaticMarkup(<LocaleProvider><StudioChooser /></LocaleProvider>));
const renderCompact = () => decode(renderToStaticMarkup(<LocaleProvider><StudioChooser compact /></LocaleProvider>));
const cardCount = (html: string) => (html.match(/class="studio-card"/g) ?? []).length;

/** Static markup escapes entities; decode so localized copy such as
 *  "Health & Wellness" is asserted in the same form the copy module declares. */
function decode(html: string) {
  return html.replaceAll("&amp;", "&").replaceAll("&quot;", '"');
}

describe("Health Care chooser tile", () => {
  it("exposes the Health Care tile on the full Create chooser", () => {
    const html = renderFull();

    expect(healthCareTileHref).toBe("/create/health");
    expect(html).toContain(`href="${healthCareTileHref}"`);
    expect(html).toContain('data-health-care-tile="true"');
    expect(html).toContain('data-health-care-category="true"');
    expect(html).toContain(`>${healthCareCopy("en").category}</h3>`);
    expect(html).toContain(`<strong>${healthCareCopy("en").title}</strong>`);
    expect(html).toContain(`<small>${healthCareCopy("en").status}</small>`);
  });

  it("exposes the same tile in the compact home launcher", () => {
    const html = renderCompact();

    expect(html).toContain(`href="${healthCareTileHref}"`);
    expect(html).toContain('data-health-care-tile="true"');
    expect(html).toContain(`<strong>${healthCareCopy("en").title}</strong>`);
  });

  it("counts the Health Care tile in the displayed Create total", () => {
    const html = renderFull();
    const displayed = Number(/class="studio-chooser-count">(\d+)</.exec(html)?.[1] ?? -1);

    expect(cardCount(html)).toBe(10);
    expect(displayed, "the badge matches the rendered cards").toBe(cardCount(html));
  });

  it("localizes the tile and its category for every supported locale", () => {
    for (const locale of locales) {
      const copy = healthCareCopy(locale);
      expect(copy.category.trim().length, `category for ${locale}`).toBeGreaterThan(0);
      expect(copy.title.trim().length, `title for ${locale}`).toBeGreaterThan(0);
      expect(copy.status.trim().length, `status for ${locale}`).toBeGreaterThan(0);
      expect(healthCareDirection(locale), `direction for ${locale}`)
        .toBe(locale === "en" ? "ltr" : "rtl");
      expect(healthCareLanguageTag(locale), `language tag for ${locale}`)
        .toBe(locale === "ku" ? "ckb" : locale);
    }
  });

  it("keeps the tile navigation-only with no medical capture affordance", () => {
    const html = renderFull() + renderCompact();

    expect(html).not.toContain("<form");
    expect(html).not.toContain("<input");
    expect(html).not.toContain("<textarea");
    expect(html).not.toContain('type="file"');
    expect(html).not.toContain("/api/");
    expect(html).not.toMatch(/upload|analyze my|prescription/i);
  });
});
