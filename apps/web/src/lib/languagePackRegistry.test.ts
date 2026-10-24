import { describe, expect, it } from "vitest";
import { defaultLanguagePackRegistry, languagePackDirection, languagePackManifest, languagePackTag, languagePackIds, translateLanguagePack, LanguagePackRegistry } from "./languagePackRegistry";

describe("language-pack registry", () => {
  it("publishes versioned built-in manifests including the partial zh-CN pack", () => {
    expect(languagePackIds).toEqual(["en", "ar", "ku", "zh-CN"]);
    const manifest = languagePackManifest("zh-CN");
    expect(manifest.schemaVersion).toBe(1);
    expect(manifest.version).toBe("0.1.0");
    expect(manifest.languageTag).toBe("zh-CN");
    expect(manifest.direction).toBe("ltr");
    expect(manifest.completeness).toBe("partial");
    expect(manifest.reviewState).toBe("needs-human-review");
    expect(manifest.reviewFlags.some((flag) => flag.code === "incomplete-coverage")).toBe(true);
  });

  it("translates priority Chinese copy without losing Unicode text", () => {
    expect(translateLanguagePack("zh-CN", "auth.loginTitle")).toBe("欢迎回来");
    expect(translateLanguagePack("zh-CN", "movie.title")).toBe("电影工作室");
    expect(translateLanguagePack("zh-CN", "movie.sceneCount", { count: "3" })).toBe("3 scenes");
  });

  it("falls back to English for incomplete pack coverage and never exposes a missing key as a translation", () => {
    expect(translateLanguagePack("zh-CN", "navigation.home")).toBe("首页");
    expect(translateLanguagePack("zh-CN", "movie.sceneCount", { count: "3" })).toBe("3 scenes");
    expect(translateLanguagePack("zh-CN", "missing.key")).toBe("missing.key");
    expect(translateLanguagePack("unknown", "navigation.home")).toBe("Home");
  });

  it("exposes canonical document language and direction for LTR and RTL packs", () => {
    expect(languagePackTag("zh-CN")).toBe("zh-CN");
    expect(languagePackDirection("zh-CN")).toBe("ltr");
    expect(languagePackDirection("ar")).toBe("rtl");
    expect(languagePackDirection("ku")).toBe("rtl");
  });

  it("supports installing a versioned partial plugin while preserving fallback safety", () => {
    const registry = new LanguagePackRegistry([defaultLanguagePackRegistry.get("en")!]);
    registry.install({
      manifest: {
        schemaVersion: 1,
        id: "x-test",
        version: "1.2.3",
        displayName: "Test language",
        nativeName: "Test",
        languageTag: "x-Test",
        direction: "ltr",
        fallback: "en",
        completeness: "partial",
        reviewState: "needs-human-review",
        reviewFlags: [{ code: "incomplete-coverage", scope: "pack" }],
      },
      translations: { "navigation.home": "Test home" },
    });
    expect(registry.translate("x-test", "navigation.home")).toBe("Test home");
    expect(registry.translate("x-test", "navigation.account")).toBe("Account");
  });
});
