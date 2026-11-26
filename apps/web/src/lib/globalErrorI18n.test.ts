import { describe, expect, it } from "vitest";
import { localeDirection, localeTag, translate } from "./i18n";
import { readRootErrorLocale } from "./globalErrorI18n";

describe("root error locale recovery", () => {
  const storage = (value: string | null): Pick<Storage, "getItem"> => ({ getItem: () => value });

  it("keeps an already-rendered RTL document locale authoritative", () => {
    expect(readRootErrorLocale("ckb", storage("ar"))).toBe("ku");
    expect(readRootErrorLocale("ar", storage("ku"))).toBe("ar");
  });

  it("restores an anonymous persisted locale when the document is still English", () => {
    expect(readRootErrorLocale("en", storage("ar"))).toBe("ar");
    expect(readRootErrorLocale("en", storage("ku"))).toBe("ku");
  });

  it("fails closed to English for unsupported or unavailable state", () => {
    expect(readRootErrorLocale("fr", storage("not-supported"))).toBe("en");
    expect(readRootErrorLocale(null, null)).toBe("en");
  });

  it("keeps the recovered copy and document direction locale-safe", () => {
    for (const locale of ["en", "ar", "ku"] as const) {
      expect(translate(locale, "error.title")).not.toBe("error.title");
      expect(localeTag(locale)).toBe(locale === "ku" ? "ckb" : locale);
      expect(localeDirection(locale)).toBe(locale === "en" ? "ltr" : "rtl");
    }
  });
});
