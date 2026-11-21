import { describe, expect, it } from "vitest";
import { locales, translations } from "./i18n";

// Synthetic completeness harness for the forgot-password release re-cut
// (fresh-main integration of #686 + stacked acceptance #788/#808/#721).
// It pins the exact i18n contract that the merge resolution must preserve:
// every password-recovery key present for every launch locale, placeholder
// parity across locales, and the independently-landed main-lane key
// (onboarding.zhPreviewNote) that the conflict resolution re-inserted.

const recoveryKeys = [
  "auth.forgotPassword",
  "auth.forgotEyebrow",
  "auth.forgotTitle",
  "auth.forgotSubtitle",
  "auth.sendResetLink",
  "auth.resetRequestSent",
  "auth.recoveryUnavailable",
  "auth.resetTitle",
  "auth.resetSubtitle",
  "auth.newPassword",
  "auth.resetPassword",
  "auth.resetSuccess",
  "auth.resetInvalid",
  "auth.resetLinkMissing",
  "auth.backToLogin",
  "auth.passwordResetWorking",
] as const;

const placeholders = (value: string) =>
  [...value.matchAll(/\{[A-Za-z0-9_]+\}/g)].map((m) => m[0]).sort();

describe("password recovery i18n completeness", () => {
  it("ships every recovery string for every launch locale", () => {
    for (const locale of locales) {
      const dict = translations[locale];
      for (const key of recoveryKeys) {
        expect(dict[key], `${locale}:${key}`).toBeTruthy();
        expect(typeof dict[key], `${locale}:${key}`).toBe("string");
      }
    }
  });

  it("keeps interpolation placeholders identical for each recovery key across locales", () => {
    for (const key of recoveryKeys) {
      const baseline = placeholders(translations.en[key]);
      for (const locale of locales) {
        expect(placeholders(translations[locale][key]), `${locale}:${key} placeholders`).toEqual(baseline);
      }
    }
  });

  it("preserves the main-lane onboarding.zhPreviewNote key alongside the recovery additions", () => {
    // Regression pin for the single mechanical conflict resolved in this
    // re-cut: the language-pack foundation (#1034) added this key while the
    // recovery lane re-wrapped the same packed line. The union resolution
    // must keep BOTH sides.
    for (const locale of locales) {
      expect(translations[locale]["onboarding.zhPreviewNote"], `${locale}:onboarding.zhPreviewNote`).toBeTruthy();
    }
  });

  it("keeps the full launch-locale key sets in parity", () => {
    const enKeys = Object.keys(translations.en).sort();
    for (const locale of locales) {
      expect(Object.keys(translations[locale]).sort(), `${locale} key set`).toEqual(enKeys);
    }
  });
});
