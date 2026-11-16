import { describe, expect, it } from "vitest";
import { defaultLanguagePackRegistry, languagePackDirection, languagePackManifest, languagePackTag, languagePackIds, translateLanguagePack, LanguagePackRegistry } from "./languagePackRegistry";

describe("language-pack registry", () => {
  it("publishes versioned built-in manifests including the partial zh-CN pack", () => {
    expect(languagePackIds).toEqual(["en", "ar", "ku", "zh-CN"]);
    const manifest = languagePackManifest("zh-CN");
    expect(manifest.schemaVersion).toBe(1);
    expect(manifest.version).toBe("0.2.0");
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

  it("refuses a silent re-install that would retarget dependent fallback chains", () => {
    const base = defaultLanguagePackRegistry.get("en")!;
    const registry = new LanguagePackRegistry([base]);
    const pack = (version: string, home: string) => ({
      manifest: { ...base.manifest, version },
      translations: { ...base.translations, "navigation.home": home },
    });
    expect(() => registry.install(pack("2.0.0", "Hijacked"))).toThrow(/already installed/);
    expect(registry.translate("en", "navigation.home")).not.toBe("Hijacked");
    expect(registry.install(pack("2.0.0", "Reviewed"), { replaceVersion: base.manifest.version }).version).toBe("2.0.0");
  });

  it("rejects ids that are unsafe as a storage-key segment and unsupported directions", () => {
    const registry = new LanguagePackRegistry();
    const manifest = {
      schemaVersion: 1,
      version: "1.0.0",
      displayName: "Probe",
      nativeName: "Probe",
      languageTag: "x-Probe",
      direction: "ltr",
      fallback: null,
      completeness: "complete",
      reviewState: "reviewed",
      reviewFlags: [],
    } as const;
    for (const id of ["", " leading-space", "a".repeat(40), "zh/../en", "中文"]) {
      expect(() => registry.install({ manifest: { ...manifest, id }, translations: { "navigation.home": "x" } }), `id ${JSON.stringify(id)}`).toThrow(/safe identifier/);
    }
    expect(() => registry.install({ manifest: { ...manifest, id: "x-ok", direction: "ttb" as "ltr" }, translations: { "navigation.home": "x" } })).toThrow(/unsupported direction/);
  });

  it("never interpolates an inherited Object.prototype member into visible text", () => {
    const registry = new LanguagePackRegistry([defaultLanguagePackRegistry.get("en")!]);
    registry.install({
      manifest: { ...defaultLanguagePackRegistry.get("en")!.manifest, id: "x-proto", fallback: "en" },
      translations: { "proto.toString": "value {toString}", "proto.constructor": "value {constructor}", "proto.safe": "value {count}" },
    });
    expect(registry.translate("x-proto", "proto.toString")).toBe("value {toString}");
    expect(registry.translate("x-proto", "proto.constructor")).toBe("value {constructor}");
    expect(registry.translate("x-proto", "proto.safe", { count: "7" })).toBe("value 7");
    // A key that only exists on Object.prototype must resolve as missing, not as a function.
    expect(registry.translate("x-proto", "toString")).toBe("toString");
    expect(registry.translate("x-proto", "constructor")).toBe("constructor");
  });

  it("reports an uninstalled fallback target instead of stranding every miss", () => {
    const registry = new LanguagePackRegistry();
    registry.install({
      manifest: { schemaVersion: 1, id: "x-orphan", version: "1.0.0", displayName: "Orphan", nativeName: "Orphan", languageTag: "x-Orphan", direction: "ltr", fallback: "x-missing", completeness: "partial", reviewState: "needs-human-review", reviewFlags: [] },
      translations: { "navigation.home": "Orphan home" },
    });
    expect(registry.missingFallbacks()).toEqual(["x-missing"]);
    expect(registry.translate("x-orphan", "navigation.home")).toBe("Orphan home");
    expect(registry.translate("x-orphan", "navigation.account")).toBe("navigation.account");
  });

  it("keeps the built-in registry free of fallback gaps and coverage drift", () => {
    expect(defaultLanguagePackRegistry.missingFallbacks()).toEqual([]);
    expect(defaultLanguagePackRegistry.coverageDrift("zh-CN")).toEqual([]);
  });
});
