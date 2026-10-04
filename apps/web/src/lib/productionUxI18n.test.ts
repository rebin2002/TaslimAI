import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { localeDirection, locales, translate } from "./i18n";

const presentationKeys = [
  "presentation.stageTitle",
  "presentation.canvasTitle",
  "presentation.canvasDescription",
  "presentation.privateWorkspace",
  "presentation.slideFirst",
  "presentation.library",
  "presentation.recentTitle",
  "presentation.selectedSlide",
  "presentation.previousSlide",
  "presentation.nextSlide",
  "presentation.privateByDefault",
  "presentation.privateNote",
  "presentation.progress.brief",
  "presentation.progress.structure",
  "presentation.progress.slides",
  "error.title",
  "error.description",
  "error.retry",
  "error.openAssets",
];

describe("production UX localization and accessibility guardrails", () => {
  it("keeps shared error and Presentation Studio copy translated in all launch locales", () => {
    for (const locale of locales) {
      for (const key of presentationKeys) expect(translate(locale, key)).not.toBe(key);
      expect(localeDirection(locale)).toBe(locale === "en" ? "ltr" : "rtl");
    }
  });

  it("does not regress visible Presentation or Social loading copy into English-only fallbacks", () => {
    const presentation = readFileSync(new URL("../components/PresentationStudioView.tsx", import.meta.url), "utf8");
    const social = readFileSync(new URL("../components/SocialStudioView.tsx", import.meta.url), "utf8");
    expect(presentation).not.toContain("Build the story before the slides");
    expect(presentation).not.toContain("Your presentation starts here");
    expect(presentation).not.toContain('aria-label=\"Previous slide\"');
    expect(presentation).toContain('role=\"progressbar\"');
    expect(social).not.toContain(">Loading...</p>");
    expect(social).toContain('role=\"progressbar\"');
  });
});
