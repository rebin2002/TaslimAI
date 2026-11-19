/**
 * Provider-free pins for the accessibility of the cited-source links in a completed
 * Research Studio report (test-only, DRAFT - one assertion family is expected RED).
 *
 * A finished report credits its claims with an `[S1]`-shaped marker in the body text and
 * offers exactly one interactive per-source control: the icon anchor in the evidence rail
 * (apps/web/src/components/ResearchStudioView.tsx, EvidenceRail). A keyboard or
 * screen-reader reader therefore reaches a source only through that anchor, and the only
 * thing such a reader hears is the anchor's accessible name.
 *
 * Scope boundaries with the citation-integrity lanes, none of which reach this render
 * surface: #254 and #303 are provider-side (a citation is required on factual blocks, and
 * evidence is attributed to the right span); #866 hardens `ResearchCitationUrlPolicy` at
 * persistence; #869 validates unresolved inline markers in the draft; the merged #967
 * mirrors that URL gate on the rail, and the merged #976 names the report live region.
 * Those decide *whether* an anchor exists and *which* locator it may carry. Nothing
 * verifies what the anchor announces once it exists, which is the consumer surface a
 * reader without a mouse actually perceives. This file shares no path with any of them.
 *
 * No provider, no network, no stored credential, no user data: every case renders
 * EvidenceRail directly from synthetic rows and reads the shipped translation table.
 */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { EvidenceRail } from "./ResearchStudioView";
import { localeDirection, translations, type Locale } from "@/lib/i18n";
import type { ResearchSource } from "@/lib/api";

const LOCALES: Locale[] = ["en", "ar", "ku"];

/** The real shipped catalogue, so a missing key cannot hide behind an identity stub. */
const localize = (locale: Locale) => (key: string) => translations[locale][key] ?? key;

function source(overrides: Partial<ResearchSource> = {}): ResearchSource {
  return {
    citationId: "S1", url: "https://example.gov/report", title: "Federal Solar Report",
    domain: "example.gov", publisher: "Government Office", publishedAt: null,
    retrievedAt: "2026-11-19T00:00:00Z", sourceType: "web",
    snippet: "Installed capacity doubled.", searchQuery: null, rank: 1, isSelected: true,
    evidence: [{ topic: "Capacity", excerpt: "Grid-scale capacity doubled.", context: null, publishedAt: null }],
    ...overrides,
  };
}

/** Arabic and Sorani rows, because the same report is read right-to-left in both. */
const MULTILINGUAL = [
  source(),
  source({ citationId: "S2", url: "https://unhcr.org/ar/report", title: "تقرير الطاقة المتجددة", domain: "unhcr.org" }),
  source({ citationId: "S3", url: "https://krdstat.gov/report", title: "ڕاپۆرتی وزەی نوێبووەوە", domain: "krdstat.gov" }),
];

const rail = (sources: ResearchSource[], locale: Locale = "en") =>
  renderToStaticMarkup(<EvidenceRail sources={sources} t={localize(locale)} />);

/** What the browser exposes for each rail anchor, in render order. */
function anchorNames(html: string) {
  return [...html.matchAll(/<a\b([^>]*)>/g)].map(([, attributes]) => ({
    label: attributes.match(/aria-label="([^"]*)"/)?.[1] ?? "",
    hasHref: /\bhref="/.test(attributes),
  }));
}

describe("Research Studio cited-source links", () => {
  it("gives every cited-source anchor an href, the only thing that makes an anchor focusable", () => {
    // An <a> without href is inert text: it never enters the tab order, so a keyboard
    // reader cannot reach the source at all, however well it is named.
    const anchors = anchorNames(rail(MULTILINGUAL));
    expect(anchors).toHaveLength(3);
    for (const anchor of anchors) expect(anchor.hasHref).toBe(true);
  });

  it("names each anchor with the localized verb instead of the bare icon", () => {
    // The anchor's only content is an <svg> glyph, so without a label a screen reader
    // announces an unnamed link whose purpose is only guessable from the neighbouring title.
    for (const locale of LOCALES) {
      const anchors = anchorNames(rail(MULTILINGUAL, locale));
      expect(anchors).toHaveLength(3);
      const verb = translations[locale]["research.openSource"];
      expect(verb).toBeTruthy();
      for (const anchor of anchors) {
        expect(anchor.label.length).toBeGreaterThan(0);
        // A silent fall back to the raw key is the failure class this lane has hit before:
        // the reader would hear "research dot openSource colon".
        expect(anchor.label).not.toContain("research.");
        expect(anchor.label.startsWith(verb)).toBe(true);
      }
    }
  });

  it("keeps the source attribution in the name a reader hears", () => {
    // Format-agnostic on purpose: the name must attribute the document, whatever its
    // punctuation. Titles stay verbatim in Arabic and Sorani, never transliterated and never
    // reduced to the domain the card shows beside them.
    for (const locale of LOCALES) {
      const anchors = anchorNames(rail(MULTILINGUAL, locale));
      const verb = translations[locale]["research.openSource"];
      anchors.forEach((anchor, index) => {
        expect(anchor.label).toContain(verb);
        expect(anchor.label).toContain(MULTILINGUAL[index].title);
      });
    }
  });

  it("carries the citation identifier in the accessible name so a marker resolves to its source", () => {
    // RED on current main (41f9fcc), en/ar/ku alike. The name is "<verb>: <title>" with no
    // identifier, while the body credits the very same source as [S1]. The citation ID is the
    // only key shared between the two surfaces, so a reader who cannot see the card order -
    // or who browses the links list on its own, where every entry starts with the identical
    // generic verb - has nothing to match a claim back to the document that supports it.
    // Minimal remedy: fold source.citationId into the existing aria-label. Nothing else about
    // the anchor may change: not href, target, rel, nor the isSafeCitationLocator gate.
    for (const locale of LOCALES) {
      const anchors = anchorNames(rail(MULTILINGUAL, locale));
      expect(anchors).toHaveLength(3);
      anchors.forEach((anchor, index) => {
        expect(anchor.label).toContain(MULTILINGUAL[index].citationId);
      });
    }
  });

  it("shows the marker and names the same document for every source", () => {
    // Attribution has to survive on both surfaces at once: the rail card renders [S1]
    // verbatim and its link opens exactly that locator.
    MULTILINGUAL.forEach((expected, index) => {
      const html = rail(MULTILINGUAL);
      expect(html).toContain(`>[${expected.citationId}]<`);
      expect(html).toContain(expected.title);
      expect(html).toContain(`href="${expected.url}"`);
      expect(anchorNames(html)[index].label).toContain(expected.title);
    });
  });

  it("isolates the citation markers from RTL reordering in Arabic and Sorani", () => {
    // "[S1] [S2]" is a Latin token inside an RTL paragraph: unshielded, the brackets mirror
    // and the order reverses. The shipped rule pins the marker run to ltr under dir="rtl",
    // and the identifier is not retyped anywhere on the way to the DOM.
    const css = readFileSync(fileURLToPath(new URL("../app/globals.css", import.meta.url)), "utf8");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
    expect(localeDirection("en")).toBe("ltr");
    expect(css).toContain('[dir="rtl"] .research-citation-list { direction: ltr;');
    expect(rail([source({ citationId: "S12" })])).toContain(">[S12]<");
  });

  it("refuses to leave an unnamed focusable control when the locator is refused", () => {
    // The gate itself is the merged #967 lane; this pin protects only its accessibility half:
    // a refused locator must remove the anchor entirely rather than leave an href-less or
    // label-less stub sitting in the tab order under a name that promises a source.
    const html = rail([source({ url: "https://taslim.ai:secret@evil.example/report" })]);
    expect(anchorNames(html)).toHaveLength(0);
    expect(html).not.toMatch(/<a\b/);
  });
});
