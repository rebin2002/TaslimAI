import { describe, expect, it } from "vitest";
import { localeDirection, locales } from "./i18n";
import {
  formatMovieDeliveryDate,
  formatMovieDeliveryNumber,
  movieDeliveryKeys,
  movieDeliveryStatusLabel,
  movieDeliveryText,
} from "./movieDeliveryI18n";

describe("Movie delivery localization", () => {
  it("keeps the delivery catalog complete in every launch locale", () => {
    for (const locale of locales) {
      for (const key of movieDeliveryKeys) {
        const value = movieDeliveryText(locale, key);
        expect(value, `${locale}:${key}`).not.toBe("");
        if (locale !== "en") expect(value, `${locale}:${key}`).not.toBe(key);
      }
    }
  });

  it("localizes delivery statuses and keeps RTL metadata aligned", () => {
    expect(movieDeliveryStatusLabel("ar", "Ready")).toBe("جاهز");
    expect(movieDeliveryStatusLabel("ku", "Failed")).toBe("شکستی هێنا");
    expect(movieDeliveryStatusLabel("en", "UnknownStatus")).toBe(
      "UnknownStatus",
    );
    expect(locales.map((locale) => localeDirection(locale))).toEqual([
      "ltr",
      "rtl",
      "rtl",
    ]);
  });

  it("uses Arabic-Indic digits for Arabic and Sorani delivery counts", () => {
    expect(formatMovieDeliveryNumber("en", 1200)).toBe("1,200");
    expect(formatMovieDeliveryNumber("ar", 1200)).toContain("١");
    expect(formatMovieDeliveryNumber("ku", 1200)).toContain("١");
  });

  it("formats persisted assembly dates through the selected locale", () => {
    const value = "2026-10-05T17:00:00.000Z";
    expect(formatMovieDeliveryDate("en", value)).toContain("2026");
    expect(formatMovieDeliveryDate("ar", value)).not.toBe(
      formatMovieDeliveryDate("en", value),
    );
    expect(formatMovieDeliveryDate("ku", value)).not.toBe(
      formatMovieDeliveryDate("en", value),
    );
  });
});
