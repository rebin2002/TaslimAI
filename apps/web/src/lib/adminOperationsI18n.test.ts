import { describe, expect, it } from "vitest";
import { adminOperationsKeys, adminOperationsTranslations, formatAdminBytes, formatAdminDateTime, formatAdminMoney, formatAdminMinutes, formatAdminNumber, translateAdminOperations } from "./adminOperationsI18n";
import { locales } from "./i18n";

describe("admin operations localization", () => {
  it("keeps the isolated catalog in parity for every launch locale", () => {
    for (const locale of locales) {
      expect(Object.keys(adminOperationsTranslations[locale]).sort(), `${locale} catalog`).toEqual([...adminOperationsKeys].sort());
    }
  });

  it("uses Arabic-Indic digits for Arabic and Sorani operational metrics", () => {
    expect(formatAdminNumber(1234567, "en")).toBe("1,234,567");
    expect(formatAdminNumber(1234567, "ar")).toContain("١");
    expect(formatAdminNumber(1234567, "ku")).toContain("١");
  });

  it("formats dates, money, byte sizes, and queue ages through the selected locale", () => {
    expect(formatAdminDateTime("2026-10-05T11:04:52Z", "ku")).toContain("٢٠٢٦");
    expect(formatAdminDateTime("not-a-date", "ar")).toBe(translateAdminOperations("ar", "notRecorded"));
    expect(formatAdminMoney(12.5, "USD", "ar")).toContain("$");
    expect(formatAdminBytes(1024 * 1024 * 2.5, "ku")).toContain("MB");
    expect(formatAdminMinutes(125, "ar")).toContain("٢");
  });
});
