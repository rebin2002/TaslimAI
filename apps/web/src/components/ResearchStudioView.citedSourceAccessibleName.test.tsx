/**
 * Targeted pins for the accessible name of the cited-source links in a completed Research
 * Studio report (apps/web/src/components/ResearchStudioView.tsx, EvidenceRail).
 *
 * Companion to the test-only RED lane #1187, which establishes WHY the name must carry the
 * citation identifier. This file pins the SHAPE of the remedy so a later refactor cannot
 * regress it, and the two properties the remedy has to hold while it does so: it must not
 * re-bracket the marker (the merged #976 lane counts literal `[S1]` occurrences in the
 * rendered report, so a bracketed name silently breaks that pin) and it must not disturb the
 * locator surface mirrored by the merged #967/#866 lanes (href, target, rel and the
 * isSafeCitationLocator gate stay exactly as they were).
 *
 * No provider, no network, no stored credential, no user data: every case renders
 * EvidenceRail from synthetic rows against the shipped translation catalogue.
 */
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { EvidenceRail } from "./ResearchStudioView";
import { localeDirection, translations, type Locale } from "@/lib/i18n";
import type { ResearchSource } from "@/lib/api";

const LOCALES: Locale[] = ["en", "ar", "ku"];

/** The real shipped catalogue, so a key added for one locale cannot hide an absent sibling. */
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

const MULTILINGUAL = [
  source(),
  source({ citationId: "S2", url: "https://unhcr.org/ar/report", title: "تقرير الطاقة المتجددة", domain: "unhcr.org" }),
  source({ citationId: "S3", url: "https://krdstat.gov/report", title: "ڕاپۆرتی وزەی نوێبووەوە", domain: "krdstat.gov" }),
];

const rail = (sources: ResearchSource[], locale: Locale = "en") =>
  renderToStaticMarkup(<EvidenceRail sources={sources} t={localize(locale)} />);

/** aria-label of each rail anchor, in render order, exactly as the browser exposes it. */
const names = (html: string) =>
  [...html.matchAll(/<a\b[^>]*aria-label="([^"]*)"/g)].map(([, label]) => label);

describe("cited-source accessible name shape", () => {
  it("reads verb, identifier, then title in every shipped locale", () => {
    // One canonical order across locales, so a reader comparing the links list against the
    // report body matches on the same token in the same place every time. The colon that
    // already separated verb and title now separates identifier and title.
    for (const locale of LOCALES) {
      const verb = localize(locale)("research.openSource");
      expect(verb).toBeTruthy();
      names(rail(MULTILINGUAL, locale)).forEach((label, index) => {
        expect(label).toBe(`${verb} ${MULTILINGUAL[index].citationId}: ${MULTILINGUAL[index].title}`);
      });
    }
  });

  it("keeps the identifier unbracketed so the rendered marker count is untouched", () => {
    // The bracketed form is the body/badge spelling, owned by #976; folding "[S1]" into the
    // name would add a fourth literal occurrence per source and break that pin. Inside a
    // name attribute the brackets are read aloud as punctuation anyway.
    const html = rail(MULTILINGUAL);
    for (const label of names(html)) {
      expect(label).not.toContain("[");
      expect(label).not.toContain("]");
    }
    expect((html.match(/\[S1\]/g) ?? [])).toHaveLength(1);
    expect((html.match(/\[S2\]/g) ?? [])).toHaveLength(1);
    expect((html.match(/\[S3\]/g) ?? [])).toHaveLength(1);
  });

  it("makes each name unique, including for two sources sharing one title", () => {
    // The failing consumer property: a links list where every entry opens with the same
    // generic verb is unusable, and duplicate titles are common when one publisher appears
    // twice. Only the identifier separates them, so the names must differ before the title.
    const duplicated = [source(), source({ citationId: "S2" })];
    const labels = names(rail(duplicated));
    expect(labels).toHaveLength(2);
    expect(new Set(labels).size).toBe(2);
    expect(labels[0]).not.toBe(labels[1]);
    // Multi-digit identifiers survive unretyped, so S10 is never announced as S1 or S0.
    const wide = names(rail([source({ citationId: "S10" })]))[0];
    expect(wide).toContain(" S10: ");
  });

  it("carries the identifier in the name only, never onto the locator", () => {
    // The label is the accessibility half of the row; the href remains the source's own
    // locator byte-for-byte, and target/rel keep the merged #967/#866 security contract.
    const html = rail(MULTILINGUAL);
    const anchors = [...html.matchAll(/<a\b([^>]*)>/g)].map(([, attributes]) => attributes);
    expect(anchors).toHaveLength(3);
    const labels = names(html);
    anchors.forEach((attributes, index) => {
      const expected = MULTILINGUAL[index];
      expect(attributes).toContain(`href="${expected.url}"`);
      expect(attributes).toContain('target="_blank"');
      expect(attributes).toContain('rel="noreferrer"');
      expect(attributes).toContain(`aria-label="${labels[index]}"`);
      expect(labels[index]).toContain(expected.citationId);
      // The identifier never leaks into the locator: href stays exactly the source's URL.
      expect(attributes.match(/href="([^"]*)"/)?.[1]).toBe(expected.url);
    });
  });

  it("drops the whole name with the anchor when the locator is refused", () => {
    // No identifier-bearing label may advertise a control that is not there: the gate removes
    // the anchor rather than leaving a named stub in the tab order.
    const html = rail([source({ url: "javascript:alert(1)" })]);
    expect(html).not.toContain("S1:");
    expect(html.match(/<a\b/g) ?? []).toHaveLength(0);
  });

  it("leaves the RTL presentation of the badge untouched", () => {
    // Arabic and Sorani render the report right-to-left while the marker run stays ltr; the
    // name is an attribute, so folding the identifier in must not change either spelling.
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
    for (const locale of ["ar", "ku"] as Locale[]) {
      const labels = names(rail(MULTILINGUAL, locale));
      expect(labels[1]).toContain("S2: تقرير الطاقة المتجددة");
      expect(labels[2]).toContain("S3: ڕاپۆرتی وزەی نوێبووەوە");
    }
    expect(rail(MULTILINGUAL)).toContain(">[S2]<");
  });
});
