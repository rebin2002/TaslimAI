import { describe, expect, it } from "vitest";
import { locales } from "./i18n";
import { movieWorldGeographyTranslations, translateMovieWorld } from "./movieWorldI18n";

describe("Movie World geography localization", () => {
  it("keeps every geography label available in English, Arabic, and Sorani", () => {
    const expectedKeys = Object.keys(movieWorldGeographyTranslations.en).sort();
    for (const locale of locales) {
      expect(Object.keys(movieWorldGeographyTranslations[locale]).sort(), `${locale} geography keys`).toEqual(expectedKeys);
      for (const key of expectedKeys as Array<keyof typeof movieWorldGeographyTranslations.en>) {
        expect(translateMovieWorld(locale, key), `${locale}:${key}`).not.toBe(key);
      }
    }
  });

  it("interpolates revision and approval counts without losing the selected locale", () => {
    expect(translateMovieWorld("ar", "geography.guideRevision", { revision: "2", continuity: "مُجزّأ" })).toContain("2");
    expect(translateMovieWorld("ku", "geography.approvedCount", { count: "3" })).toContain("3");
  });
});
