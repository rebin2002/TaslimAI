/**
 * Provider-free pins for the finished report's zero-source evidence states.
 *
 * Scope: results-page clarity and localization of the three distinct no-source
 * conditions - a genuine report that cites nothing, a source-ledger read that
 * failed and can be retried, and a ledger still being retrieved - plus the
 * completeness of their localized strings in every supported locale. Citation
 * deduplication/numbering, locator safety, and the compose picker's own file
 * message are separate lanes and are not pinned here. No provider, no network:
 * the panel is a pure function of (sources, load status).
 */
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { ResearchSourcesPanel } from "./ResearchStudioView";
import { researchSourcesPanelStatus } from "@/lib/researchStudioState";
import { locales, translate } from "@/lib/i18n";
import type { ResearchSource } from "@/lib/api";

const t = (key: string) => key;
function source(overrides: Partial<ResearchSource> = {}): ResearchSource {
  return {
    citationId: "S1", url: "https://example.org/study", title: "Field Study",
    domain: "example.org", publisher: "Example Institute", publishedAt: null,
    retrievedAt: "2026-11-17T00:00:00Z", sourceType: "web", snippet: "A finding.",
    searchQuery: null, rank: 1, isSelected: true, evidence: [], ...overrides,
  };
}
const panel = (sources: ResearchSource[], load: "loading" | "ready" | "error") =>
  renderToStaticMarkup(<ResearchSourcesPanel sources={sources} load={load} onRetry={() => undefined} t={t} />);

const resultStateKeys = [
  "research.emptyEvidence", "research.emptyEvidenceHint", "research.loadingEvidence",
  "research.sourcesLoadError", "research.sourcesLoadErrorHint", "research.retrySources",
] as const;

describe("Research report no-sources state clarity", () => {
  it("maps the three zero-source conditions and the populated case to distinct panel states", () => {
    expect(researchSourcesPanelStatus("loading", 0)).toBe("loading");
    expect(researchSourcesPanelStatus("error", 0)).toBe("error");
    expect(researchSourcesPanelStatus("ready", 0)).toBe("empty");
    expect(researchSourcesPanelStatus("loading", 3)).toBe("populated");
    expect(researchSourcesPanelStatus("error", 2)).toBe("populated");
  });

  it("offers a localized retry when the ledger read failed instead of claiming the report cites nothing", () => {
    const html = panel([], "error");
    expect(html).toContain("research.sourcesLoadError");
    expect(html).toContain("research.sourcesLoadErrorHint");
    expect(html).toContain("research.retrySources");
    expect(html).toContain("<button");
    expect(html).toContain('role="status"');
    // The failure must not fall through to the picker's file message or the true-empty copy.
    expect(html).not.toContain("research.noSources");
    expect(html).not.toContain("research.emptyEvidence");
  });

  it("explains a genuine no-citation report with its own copy and no error affordance", () => {
    const html = panel([], "ready");
    expect(html).toContain("research.emptyEvidence");
    expect(html).toContain("research.emptyEvidenceHint");
    expect(html).toContain('role="status"');
    expect(html).not.toContain("<button");
    expect(html).not.toContain("research.noSources");
    expect(html).not.toContain("research.sourcesLoadError");
  });

  it("shows retrieval-in-progress copy rather than a premature empty claim", () => {
    const html = panel([], "loading");
    expect(html).toContain("research.loadingEvidence");
    expect(html).not.toContain("research.emptyEvidence");
    expect(html).not.toContain("research.sourcesLoadError");
    expect(html).not.toContain("research.noSources");
  });

  it("keeps the evidence rail for a populated report whatever the ledger fetch status is", () => {
    for (const load of ["loading", "ready", "error"] as const) {
      const html = panel([source()], load);
      expect(html).toContain("research.evidenceRail");
      expect(html).not.toContain("research.emptyEvidence");
      expect(html).not.toContain("research.sourcesLoadError");
      expect(html).not.toContain("research.loadingEvidence");
    }
  });

  it("localizes every report-state string in every supported locale", () => {
    for (const locale of locales) {
      for (const key of resultStateKeys) {
        const value = translate(locale, key);
        expect(value, `${locale}:${key}`).toBeTruthy();
        expect(value, `${locale}:${key}`).not.toBe(key);
      }
    }
    // Every new report message is translated into Arabic and Kurdish, not mirrored from English.
    for (const key of resultStateKeys) {
      expect(translate("ar", key)).not.toBe(translate("en", key));
      expect(translate("ku", key)).not.toBe(translate("en", key));
    }
  });

  it("keeps the compose picker message owned by its own lane", () => {
    // The report page stopped reusing this file-attachment sentence; the picker itself is unchanged.
    expect(translate("en", "research.noSources")).toContain("source files");
  });
});
