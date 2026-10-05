import { describe, expect, it } from "vitest";
import { localeDirection, localeTag, locales, translate, translations } from "./i18n";

describe("translation catalog integrity", () => {
  it("keeps the exact same keys in every supported locale", () => {
    const expectedKeys = Object.keys(translations.en).sort();

    for (const locale of locales) {
      expect(Object.keys(translations[locale]).sort(), `${locale} translation keys`).toEqual(expectedKeys);
    }
  });

  it("does not fall back to a key or the wrong document direction", () => {
    for (const locale of locales) {
      for (const key of Object.keys(translations.en)) {
        expect(translate(locale, key), `${locale}:${key}`).not.toBe(key);
      }
      expect(localeDirection(locale)).toBe(locale === "en" ? "ltr" : "rtl");
    }
  });

  it("uses canonical language tags for document metadata and browser formatting", () => {
    expect(localeTag("en")).toBe("en");
    expect(localeTag("ar")).toBe("ar");
    expect(localeTag("ku")).toBe("ckb");
    expect(translate("ar", "missing.translation.key")).toBe("missing.translation.key");
    expect(translate("ar", "navigation.home")).toBe(translations.ar["navigation.home"]);
  });
});
