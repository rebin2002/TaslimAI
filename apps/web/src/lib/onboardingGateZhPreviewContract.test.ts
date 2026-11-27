import { describe, expect, it } from "vitest";
import {
  defaultLanguagePackRegistry,
  isLanguagePackInstalled,
  languagePackDirection,
  translateLanguagePack,
} from "./languagePackRegistry";
import { translations } from "./i18n";

// Provider-free contract over the single OnboardingGate.tsx hunk where the
// accessible-onboarding lane (#846) and the installable language-pack lane
// (#856) overlap: the zh-CN preview note must be served by the shared t()
// boundary the #846 gate already uses, never by an unmanaged literal.
const NOTE_KEY = "onboarding.zhPreviewNote";

describe("OnboardingGate zh-CN preview note cross-lane contract", () => {
  it("resolves the note through t() for every shipped pack, without providers", () => {
    for (const id of ["en", "ar", "ku", "zh-CN"]) {
      expect(isLanguagePackInstalled(id), `pack ${id} is installed`).toBe(true);
      const value = translateLanguagePack(id, NOTE_KEY);
      expect(value, `pack ${id} does not render the raw key`).not.toBe(NOTE_KEY);
      expect(value.length, `pack ${id} renders non-empty copy`).toBeGreaterThan(0);
    }
  });

  it("carries Arabic and Kurdish Sorani copy in the catalogs the gate ships", () => {
    expect(translateLanguagePack("ar", NOTE_KEY)).toBe(translations.ar[NOTE_KEY]);
    expect(translateLanguagePack("ku", NOTE_KEY)).toBe(translations.ku[NOTE_KEY]);
    expect(translations.ar[NOTE_KEY]).not.toBe(translations.en[NOTE_KEY]);
    expect(translations.ku[NOTE_KEY]).not.toBe(translations.en[NOTE_KEY]);
    // Sorani and Arabic stay in their own scripts; no placeholder drift.
    for (const value of [translations.ar[NOTE_KEY], translations.ku[NOTE_KEY]]) {
      expect(value).not.toMatch(/[{}]/);
    }
  });

  it("falls the untranslated zh-CN note back to the English root without stranding it", () => {
    const zh = defaultLanguagePackRegistry.get("zh-CN");
    expect(zh, "zh-CN pack is installed").toBeDefined();
    // The partial pack claims no onboarding coverage, so the preview note is
    // English-fallback copy by the pack's own reviewFlags; adding a value
    // here without a manifest ledger entry must stay the failure mode this
    // test refuses.
    expect(Object.prototype.hasOwnProperty.call(zh!.translations, NOTE_KEY)).toBe(false);
    expect(translateLanguagePack("zh-CN", NOTE_KEY)).toBe(translations.en[NOTE_KEY]);
    expect(defaultLanguagePackRegistry.missingFallbacks()).toEqual([]);
  });

  it("pins the gate's direction rule for the RTL and LTR preview profiles", () => {
    // OnboardingGate swaps ArrowRight/ArrowLeft from languagePackDirection;
    // the ar/ku lanes stay RTL while en and the zh-CN preview stay LTR.
    expect(languagePackDirection("ar")).toBe("rtl");
    expect(languagePackDirection("ku")).toBe("rtl");
    expect(languagePackDirection("en")).toBe("ltr");
    expect(languagePackDirection("zh-CN")).toBe("ltr");
  });
});
