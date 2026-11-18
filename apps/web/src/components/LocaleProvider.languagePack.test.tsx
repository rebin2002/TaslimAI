import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider, useLocale } from "./LocaleProvider";

function LanguagePackProbe() {
  const { languagePackIds, languagePackNames, languagePackManifest } = useLocale();
  return <div data-direction={languagePackManifest.direction} data-tag={languagePackManifest.languageTag}>
    {languagePackIds.map((id) => <span key={id}>{id}:{languagePackNames[id]}</span>)}
  </div>;
}

describe("LocaleProvider language-pack integration", () => {
  it("exposes the installable zh-CN pack alongside legacy locale callers", () => {
    const html = renderToStaticMarkup(<LocaleProvider><LanguagePackProbe /></LocaleProvider>);
    expect(html).toContain("zh-CN:简体中文");
    expect(html).toContain('data-direction="ltr"');
    expect(html).toContain('data-tag="en"');
  });
});
