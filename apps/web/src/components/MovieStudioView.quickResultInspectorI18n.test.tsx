import { existsSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { locales, translations } from "@/lib/i18n";

const studioSource = readFileSync(new URL("./MovieStudioView.tsx", import.meta.url), "utf8");
const zhCnPackPath = new URL("./languagePackZhCn.ts", import.meta.url);

// The quick-result inspector renders the saved brief next to the generated
// result. These are the same three fields the entry form already translates
// through t("movie.aspect"), t("movie.duration") and t("movie.style").
const inspectorKeys: Record<string, string> = {
  Format: "movie.aspect",
  Duration: "movie.duration",
  Style: "movie.style",
};

const inspectorBlock = (studioSource.match(/className="movie-quick-inspector"[\s\S]*?<\/aside>/) ?? [""])[0];

describe("Movie Studio quick-result inspector localization coverage", () => {
  it("localizes every visible inspector label through the translation catalogs", () => {
    expect(inspectorBlock, "quick-result inspector block must exist").not.toBe("");
    for (const label of Object.keys(inspectorKeys)) {
      // RED until the hardcoded <span>Label</span> triple is routed through t():
      // bypassing key lookup means no launch locale and no installable language
      // pack (including zh-CN from the #279 architecture) can translate it.
      expect(studioSource, `"${label}" inspector label must be a t() call`).not.toContain(`<span>${label}</span>`);
    }
  });

  it("resolves the three labels from existing catalog keys without inventing strings", () => {
    for (const key of Object.values(inspectorKeys)) {
      expect(translations.en[key], `${key} must exist in the English catalog`).toBeTruthy();
      for (const locale of locales) {
        expect(translations[locale][key], `${key} must exist for ${locale} fallback parity`).toBeTruthy();
      }
    }
    // Entry form parity: the same keys are already wired through t() there.
    expect(studioSource).toContain('t("movie.duration")');
    expect(studioSource).toContain('t("movie.aspect")');
    expect(studioSource).toContain('t("movie.style")');
  });

  it("keeps English fallback parity and finds the labels in the installable zh-CN pack when present", () => {
    // Registry semantics (#279): a missing zh-CN entry must resolve through the
    // manifest fallback to the English catalog rather than leaking a key.
    for (const key of Object.values(inspectorKeys)) {
      expect(translations.en[key], `${key} English fallback must stay non-empty`).toBeTruthy();
    }
    if (existsSync(zhCnPackPath)) {
      const pack = readFileSync(zhCnPackPath, "utf8");
      for (const key of Object.values(inspectorKeys)) {
        expect(pack, `${key} must be translatable in the zh-CN pack`).toContain(`"${key}":`);
      }
    }
  });
});
