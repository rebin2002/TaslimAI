/**
 * Provider-free pins for safe citation display in the Research Studio evidence rail.
 *
 * Scope: which persisted citation locators become a clickable external link and which do
 * not. The rail mirrors the server-side citation policy pinned by
 * apps/api.Tests/ResearchCitationUrlIngestTests.cs: absolute http(s), a host the reader
 * can verify, and no credentials in the authority. Persisted rows are served verbatim by
 * GET /api/research-generation/jobs/{id}/sources, and rows stored before that ingest
 * policy shipped are never re-filtered, so the render gate must fail closed on its own.
 * No provider, no network, no stored credential: every case renders EvidenceRail directly.
 */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { EvidenceRail, isSafeCitationLocator } from "./ResearchStudioView";
import type { ResearchSource } from "@/lib/api";

const t = (key: string) => key;

function source(overrides: Partial<ResearchSource> = {}): ResearchSource {
  return {
    citationId: "S1", url: "https://example.gov/report", title: "Federal Solar Report",
    domain: "example.gov", publisher: "Government Office", publishedAt: null,
    retrievedAt: "2026-11-17T00:00:00Z", sourceType: "web", snippet: "Installed capacity doubled.",
    searchQuery: null, rank: 1, isSelected: true, evidence: [], ...overrides,
  };
}

const rail = (sources: ResearchSource[]) => renderToStaticMarkup(<EvidenceRail sources={sources} t={t} />);

describe("Research Studio citation link safety", () => {
  it("links a clean web locator with the existing safe browsing context", () => {
    const html = rail([source()]);
    expect(html).toContain('href="https://example.gov/report"');
    expect(html).toContain('target="_blank"');
    expect(html).toContain('rel="noreferrer"');
  });

  it("refuses every credential-bearing locator the server policy rejects", () => {
    // The refused shapes from ResearchCitationUrlIngestTests: the visible prefix names one
    // host while the browser would navigate to the authority after the "@" - the citation
    // itself looks legitimate while the click does not.
    for (const url of [
      "https://taslim.ai:secret@evil.example/report",
      "https://research.taslim.ai@evil.example/report",
      "https://@evil.example/report",
      "http://taslim.ai:token@evil.example/report",
      "HTTPS://research.taslim.ai@evil.example/report",
      "ht\ttps://research.taslim.ai@evil.example/report",
      " https://@evil.example ",
    ]) {
      expect(isSafeCitationLocator(url)).toBe(false);
      const html = rail([source({ url })]);
      expect(html).not.toContain("<a ");
      expect(html).not.toContain("href=");
    }
  });

  it("keeps the citation, title and evidence visible when the locator is refused", () => {
    // Fail closed on the click only: hiding the card would silently delete evidence that
    // the report body still credits to [S1].
    const html = rail([source({ url: "https://taslim.ai:secret@evil.example/report" })]);
    expect(html).toContain("[S1]");
    expect(html).toContain("Federal Solar Report");
    expect(html).toContain("Installed capacity doubled.");
  });

  it("does not over-block: an @ below the authority stays clickable", () => {
    // The policy admits "@" outside the authority; a research locator with an at-handle in
    // the path and an email in the query is a legitimate citation.
    const url = "https://example.gov/users/@taslim?rev=a@b#section";
    expect(isSafeCitationLocator(url)).toBe(true);
    expect(rail([source({ url })])).toContain('href="https://example.gov/users/@taslim?rev=a@b#section"');
  });

  it("still refuses non-web protocols, missing hosts and unusable values", () => {
    for (const url of ["javascript:alert(1)", "data:text/html,x", "file:///etc/passwd", "https://", "   ", "not a url", null]) {
      expect(isSafeCitationLocator(url)).toBe(false);
    }
    const html = rail([source({ url: "javascript:alert(1)" })]);
    expect(html).not.toContain("<a ");
  });

  it("gates the rail anchor on the mirrored policy instead of the protocol-only helper", () => {
    const view = readFileSync(fileURLToPath(new URL("./ResearchStudioView.tsx", import.meta.url)), "utf8");
    expect(view).toContain("isSafeCitationLocator(source.url) ? <a");
    expect(view).not.toMatch(/isSafeExternalUrl\(source\.url\)/);
  });
});
