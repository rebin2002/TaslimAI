import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LocaleProvider, safeBrowserStorage, storedLanguagePack, useLocale } from "./LocaleProvider";

function LanguagePackProbe() {
  const { languagePackIds, languagePackNames, languagePackManifest } = useLocale();
  return <div data-direction={languagePackManifest.direction} data-tag={languagePackManifest.languageTag}>
    {languagePackIds.map((id) => <span key={id}>{id}:{languagePackNames[id]}</span>)}
  </div>;
}

describe("LocaleProvider language-pack integration", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("exposes the installable zh-CN pack alongside legacy locale callers", () => {
    const html = renderToStaticMarkup(<LocaleProvider><LanguagePackProbe /></LocaleProvider>);
    expect(html).toContain("zh-CN:简体中文");
    expect(html).toContain('data-direction="ltr"');
    expect(html).toContain('data-tag="en"');
  });
  it("falls back to English when the localStorage property getter is denied", () => {
    const deniedWindow = Object.defineProperty({}, "localStorage", {
      get() { throw new Error("SecurityError: blocked storage"); },
    });
    vi.stubGlobal("window", deniedWindow);

    expect(safeBrowserStorage()).toBeNull();
    expect(storedLanguagePack()).toBe("en");
  });

  it("restores an installable pack when browser storage is accessible", () => {
    const storage = {
      getItem: (key: string) => key === "taslim-language-pack" ? "zh-CN" : null,
    };
    vi.stubGlobal("window", { localStorage: storage });

    expect(safeBrowserStorage()).toBe(storage);
    expect(storedLanguagePack()).toBe("zh-CN");
  });
});
