import { describe, expect, it } from "vitest";
import { locales, localeDirection } from "./i18n";
import {
  protectedPageBootstrapStatusDirection,
  protectedPageBootstrapStatusLabelFor,
  protectedPageBootstrapStatusLabels,
  protectedPageBootstrapStatusLocale,
} from "./protectedPageI18n";

/**
 * The bootstrap boundary's announcement is the only thing a screen reader can
 * speak while the session read is stalled, so it has to exist in every locale
 * the product supports, be speakable text rather than markup noise, and resolve
 * to the language the visitor is actually reading in. These cases pin that
 * contract for the pure resolver; the mounted DOM behaviour is asserted by
 * `ProtectedPage.liveRegionAndReturnPath.test.tsx`, and the hydration-safety
 * ordering (English first, then the visible language) by the lane's
 * `ProtectedPage.test.tsx`.
 */
describe("protected-page bootstrap announcement copy", () => {
  const storage = (value: string | null): Pick<Storage, "getItem"> => ({ getItem: () => value });

  it("names the region in every supported locale, with no empty or duplicated announcement", () => {
    for (const locale of locales) {
      const label = protectedPageBootstrapStatusLabels[locale];
      expect(label, `${locale} label`).toBeTruthy();
      expect(label.trim(), `${locale} label`).toBe(label);
      expect(protectedPageBootstrapStatusLabelFor(locale), `${locale} resolver`).toBe(label);
    }
    const distinct = new Set(locales.map((locale) => protectedPageBootstrapStatusLabels[locale]));
    expect(distinct.size, "each locale announces in its own language").toBe(locales.length);
  });

  it("resolves an announcement a screen reader can render as text in each script", () => {
    // English is Latin; Arabic and Sorani Kurdish are both written in the
    // Arabic block, which is what the region must speak for those visitors.
    expect(protectedPageBootstrapStatusLabels.en).toMatch(/[A-Za-z]/);
    expect(protectedPageBootstrapStatusLabels.ar).toMatch(/[\u0600-\u06FF]/);
    expect(protectedPageBootstrapStatusLabels.ku).toMatch(/[\u0600-\u06FF]/);
    for (const locale of locales) {
      expect(protectedPageBootstrapStatusLabels[locale]).not.toMatch(/[<>&]|aria-|class=/);
    }
  });

  it("keeps the announcement in the direction the document is written", () => {
    expect(protectedPageBootstrapStatusDirection("en")).toBe(localeDirection("en"));
    expect(protectedPageBootstrapStatusDirection("ar")).toBe("rtl");
    expect(protectedPageBootstrapStatusDirection("ku")).toBe("rtl");
  });

  it("prefers the language already visible on the document over stored state", () => {
    // LocaleProvider writes the canonical tag, so Sorani arrives as `ckb`.
    expect(protectedPageBootstrapStatusLocale("ckb", storage("ar"), "en")).toBe("ku");
    expect(protectedPageBootstrapStatusLocale("ar", storage("ku"), "en")).toBe("ar");
    expect(protectedPageBootstrapStatusLocale("ar-EG", storage("ku"), "en")).toBe("ar");
    expect(protectedPageBootstrapStatusLocale("en-GB", null, "ar")).toBe("en");
  });

  it("restores the persisted choice for the window before hydration applies it", () => {
    expect(protectedPageBootstrapStatusLocale("en", storage("ar"), "en")).toBe("ar");
    expect(protectedPageBootstrapStatusLocale(undefined, storage("ku"), "en")).toBe("ku");
  });

  it("falls back through the browser language and fails closed to English", () => {
    expect(protectedPageBootstrapStatusLocale(undefined, storage(null), "ckb")).toBe("ku");
    expect(protectedPageBootstrapStatusLocale("fr", storage("not-supported"), "de")).toBe("en");
    expect(protectedPageBootstrapStatusLocale(null, null, undefined)).toBe("en");
    expect(protectedPageBootstrapStatusLabelFor(undefined as never), "unknown locale still speaks").toBe(
      protectedPageBootstrapStatusLabels.en,
    );
  });

  it("resolves without any browser globals at all", () => {
    // Server rendering and the dependency-free DOM shim both reach the boundary
    // with an absent document, so the announcement must degrade to English
    // rather than throw on the way to a loading screen.
    expect(() => protectedPageBootstrapStatusLocale(undefined, null, undefined)).not.toThrow();
    expect(protectedPageBootstrapStatusLocale(undefined, null, undefined)).toBe("en");
  });
});
