import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { languagePackDirection, languagePackIds, languagePackNames, languagePackTag } from "./languagePackRegistry";
/**
 * Regression pin for the zh-CN auth-selector residual closed on this line: the
 * Forgot Password foundation (#1034) landed the language-pack registry and the
 * account-aware LocaleProvider, but AuthForm kept rendering the legacy
 * `locale`/`localeNames` selector, so the sign-in/register screen never
 * exposed the pack surface (zh-CN included) that the rest of the product
 * adopted. This source-scan test locks the wiring ported from the original
 * onboarding line (#279) so a later refactor cannot silently revert the
 * selector, the RTL/LTR direction source, or the registration payload back to
 * the legacy locale plumbing.
 */
const authFormSource = readFileSync(new URL("../components/AuthForm.tsx", import.meta.url), "utf8");

describe("AuthForm language-pack selector wiring", () => {
  it("renders the selector from the language-pack registry surface", () => {
    expect(authFormSource).toContain('const { t, languagePackId, languagePackIds, languagePackNames, setLanguagePack } = useLocale();');
    expect(authFormSource).toContain("value={languagePackId}");
    expect(authFormSource).toContain("onChange={(event) => setLanguagePack(event.target.value)}");
    expect(authFormSource).toContain("{languagePackIds.map((item) => <option key={item} value={item}>{languagePackNames[item]}</option>)}");
  });
  it("drives layout direction from the pack registry rather than the legacy locale table", () => {
    expect(authFormSource).toContain('import { languagePackDirection } from "@/lib/languagePackRegistry";');
    expect(authFormSource).toContain("dir={languagePackDirection(languagePackId)}");
    expect(authFormSource).toContain("{languagePackDirection(languagePackId) === \"ltr\" ? <ArrowRight");
  });
  it("submits the selected pack id as the registration preferred language", () => {
    expect(authFormSource).toContain("preferredLanguage: languagePackId");
  });
  it("has no legacy locale-selector plumbing left behind", () => {
    expect(authFormSource).not.toContain('import { useLocale, localeNames, locales } from "@/components/LocaleProvider";');
    expect(authFormSource).not.toContain('import { localeDirection } from "@/lib/i18n";');
    expect(authFormSource).not.toContain("preferredLanguage: locale");
    expect(authFormSource).not.toContain("setLocale(event.target.value");
    expect(authFormSource).not.toContain("locales.map");
    expect(authFormSource).not.toContain("localeNames[");
    expect(authFormSource).not.toContain("localeDirection(");
  });
  it("keeps zh-CN selectable through the exact surface the selector renders", () => {
    expect(languagePackIds).toContain("zh-CN");
    expect(languagePackNames["zh-CN"]).toBeTypeOf("string");
    expect(languagePackDirection("zh-CN")).toBe("ltr");
    expect(languagePackTag("zh-CN")).toBe("zh-CN");
  });
});
