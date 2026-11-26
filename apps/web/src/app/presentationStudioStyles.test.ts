import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const styles = readFileSync(new URL("./globals.css", import.meta.url), "utf8");
const studioView = readFileSync(new URL("../components/PresentationStudioView.tsx", import.meta.url), "utf8");

function ruleBody(selectorList: string): string | undefined {
  for (const match of styles.matchAll(/[^{}]*\{[^{}]*\}/g)) {
    const [selector, body] = match[0].split("{");
    if (selector.trim() === selectorList.trim()) return body.trim();
  }
  return undefined;
}

const PRESENTATION_RTL_ROWS = [
  '[dir="rtl"] .presentation-studio-header',
  '[dir="rtl"] .presentation-stage-toolbar',
  '[dir="rtl"] .presentation-stage-caption',
  '[dir="rtl"] .recent-presentations-heading',
  '[dir="rtl"] .presentation-output-toolbar',
  '[dir="rtl"] .presentation-output-footer',
  '[dir="rtl"] .slide-navigator-heading',
  '[dir="rtl"] .slide-thumbnail-label',
  '[dir="rtl"] .recent-presentation-card',
].join(", ");

describe("Presentation studio RTL layout", () => {
  it("mirrors the storyboard slide-number row so the index leads in Arabic and Sorani", () => {
    const body = ruleBody(PRESENTATION_RTL_ROWS);
    expect(body).toBeDefined();
    expect(body).toContain("flex-direction: row-reverse;");
    // The thumbnail caption is `b` (slide order) then `span` (localized title).
    // Without the mirror it keeps LTR order while its reversed heading does not.
    expect(PRESENTATION_RTL_ROWS).toContain('[dir="rtl"] .slide-thumbnail-label');
  });

  it("mirrors the recent-presentation card row so the localized title stays before the trailing icon", () => {
    const body = ruleBody(PRESENTATION_RTL_ROWS);
    expect(body).toBeDefined();
    expect(body).toContain("flex-direction: row-reverse;");
    const selectors = (body === undefined ? "" : PRESENTATION_RTL_ROWS).split(",").map((s) => s.trim());
    expect(selectors).toContain('[dir="rtl"] .recent-presentation-card');
    const card = ruleBody(".recent-presentation-card");
    expect(card).toContain("display: flex;");
    expect(card).not.toContain("direction: ltr");
  });

  it("keeps the mirrored rows truncating instead of overflowing their flex parents", () => {
    const label = ruleBody(".slide-thumbnail-label");
    expect(label).toContain("min-width: 0;");
    const labelSpan = ruleBody(".slide-thumbnail-label span");
    expect(labelSpan).toContain("overflow: hidden;");
    expect(labelSpan).toContain("text-overflow: ellipsis;");
    const card = ruleBody(".recent-presentation-card");
    expect(card).toContain("min-width: 0;");
  });

  it("lets each slide canvas pick its own direction for mixed Arabic/Sorani and Latin content", () => {
    expect(studioView).toContain('className={`slide-artwork ${isTitle ? "is-title-slide" : ""} ${compact ? "is-compact" : ""}`} dir="auto"');
    expect(studioView).toContain('<div className="slide-empty-canvas" dir="auto">');
  });
});
