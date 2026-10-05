import { describe, expect, it } from "vitest";
import { localeDirection, locales } from "./i18n";
import {
  formatMovieAudioNumber,
  movieAudioKeys,
  movieAudioStatusLabel,
  movieAudioText,
} from "./movieAudioI18n";

describe("Movie Audio localization", () => {
  it("keeps the audio catalog complete and translated in every launch locale", () => {
    for (const locale of locales) {
      for (const key of movieAudioKeys) {
        const value = movieAudioText(locale, key);
        expect(value, `${locale}:${key}`).not.toBe("");
        if (locale !== "en") expect(value, `${locale}:${key}`).not.toBe(key);
      }
    }
  });

  it("localizes persisted statuses and interpolates operational values", () => {
    expect(movieAudioStatusLabel("ar", "Approved")).toBe("تمت الموافقة");
    expect(movieAudioStatusLabel("ku", "Rejected")).toBe("ڕەتکراوەتەوە");
    expect(movieAudioStatusLabel("en", "UnknownStatus")).toBe("UnknownStatus");
    expect(movieAudioText("ar", "cue", { number: "٠٣", mood: "هادئ", intensity: "٨" })).toContain("٠٣");
    expect(movieAudioText("ku", "duckingIntent", { target: "dialogue", decibels: "3", start: "1", end: "4" })).toContain("dialogue");
  });

  it("uses locale-aware digits and preserves the RTL contract", () => {
    expect(formatMovieAudioNumber("en", 1200.5)).toBe("1,200.5");
    expect(formatMovieAudioNumber("ar", 1200.5)).toContain("١");
    expect(formatMovieAudioNumber("ku", 1200.5)).toContain("١");
    expect(locales.map((locale) => localeDirection(locale))).toEqual(["ltr", "rtl", "rtl"]);
  });
});
