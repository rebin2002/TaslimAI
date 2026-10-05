import { describe, expect, it } from "vitest";
import { formatMovieDate, formatMovieDateTime, formatMovieNumber, movieIntlLocale } from "./movieLocaleFormatting";

describe("Movie workspace locale formatting", () => {
  it("maps the app's Sorani locale to the canonical Intl language tag", () => {
    expect(movieIntlLocale("en")).toBe("en");
    expect(movieIntlLocale("ar")).toBe("ar-u-nu-arab");
    expect(movieIntlLocale("ku")).toBe("ckb");
  });

  it("formats numbers using the selected locale", () => {
    expect(formatMovieNumber(1234567, "en")).toBe("1,234,567");
    expect(formatMovieNumber(1234567, "ar")).not.toBe(formatMovieNumber(1234567, "en"));
    expect(formatMovieNumber(1234567, "ku")).not.toBe(formatMovieNumber(1234567, "en"));
    expect(formatMovieNumber(3, "ar", { minimumIntegerDigits: 2 })).toBe("٠٣");
  });

  it("formats dates consistently and fails closed for invalid values", () => {
    const value = "2026-01-02T03:04:05.000Z";
    expect(formatMovieDate(value, "en")).not.toBe(formatMovieDate(value, "ar"));
    expect(formatMovieDateTime(value, "ku")).not.toBe(formatMovieDateTime(value, "en"));
    expect(formatMovieDate("not-a-date", "en")).toBe("—");
    expect(formatMovieDateTime("not-a-date", "ku")).toBe("—");
  });
});
