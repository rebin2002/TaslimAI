import { describe, expect, it } from "vitest";
import { defaultLanguagePackRegistry } from "./languagePackRegistry";
/**
 * Regression pin for the password-recovery key union landed by the Forgot
 * Password integration line (#1121): the English `auth` namespace grew from 30
 * to 46 keys while zh-CN still translates the same 20, so the shipped ledger
 * must disclose the enlarged surface as an honest 20/46 and the recovery copy
 * must stay reachable through the English fallback. The two built-in drift
 * checks guard the general invariant; this test pins the reconciled state so a
 * later edit can neither silently drop the row nor re-claim a stale total.
 */
const AUTH_RECOVERY_KEYS = [
  "auth.backToLogin",
  "auth.forgotEyebrow",
  "auth.forgotPassword",
  "auth.forgotSubtitle",
  "auth.forgotTitle",
  "auth.newPassword",
  "auth.passwordResetWorking",
  "auth.recoveryUnavailable",
  "auth.resetInvalid",
  "auth.resetLinkMissing",
  "auth.resetPassword",
  "auth.resetRequestSent",
  "auth.resetSubtitle",
  "auth.resetSuccess",
  "auth.resetTitle",
  "auth.sendResetLink",
] as const;
describe("zh-CN auth ledger after the password-recovery key union", () => {
  const en = defaultLanguagePackRegistry.get("en")!;
  const zh = defaultLanguagePackRegistry.get("zh-CN")!;
  const authKeys = Object.keys(en.translations).filter((key) => key.startsWith("auth."));
  it("ships exactly the reconciled 20/46 disclosure for the auth namespace", () => {
    expect(authKeys).toHaveLength(46);
    const translated = authKeys.filter((key) => Object.prototype.hasOwnProperty.call(zh.translations, key));
    expect(translated).toHaveLength(20);
    const row = zh.manifest.coverage?.find((entry) => entry.namespace === "auth");
    expect(row).toEqual({ namespace: "auth", translated: 20, total: 46 });
  });
  it("keeps the new recovery copy disclosed as English-fallback, not hidden", () => {
    for (const key of AUTH_RECOVERY_KEYS) {
      expect(zh.translations[key], `${key} must stay untranslated in zh-CN`).toBeUndefined();
      expect(defaultLanguagePackRegistry.translate("zh-CN", key)).toBe(en.translations[key]);
    }
    // The reconciled ledger must leave the shipped zh-CN pack drift-free.
    expect(defaultLanguagePackRegistry.coverageDrift("zh-CN")).toEqual([]);
  });
});
