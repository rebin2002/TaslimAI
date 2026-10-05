import { describe, expect, it } from "vitest";
import { localeDirection, locales } from "./i18n";
import { notFoundKeys, notFoundTranslations, translateNotFound } from "./notFoundI18n";

describe("not-found localization", () => {
  it("provides complete, non-key copy for English, Arabic, and Sorani", () => {
    for (const locale of locales) {
      for (const key of notFoundKeys) {
        expect(translateNotFound(locale, key), `${locale}:${key}`).not.toBe(key);
        expect(translateNotFound(locale, key), `${locale}:${key}`).not.toBe("");
      }
      expect(Object.keys(notFoundTranslations[locale]).sort()).toEqual([...notFoundKeys].sort());
    }
  });

  it("keeps the boundary aligned with the existing document direction contract", () => {
    expect(localeDirection("en")).toBe("ltr");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
  });
});
