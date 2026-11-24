/**
 * Issue #1267 slice 1 — how one page of scholarly results is rendered.
 *
 * Synthetic fixtures and `react-dom/server`, in the repo's established style. What is pinned
 * here is the integrity of the sentence a student reads about a record they did not verify:
 * metadata is never shown as content that was read, provenance says only that the record was
 * found, and nothing on the page names where it came from.
 */
import { describe, expect, it, vi } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import type { AcademicSourceSearchResponse } from "@/lib/api";
import { AcademicScholarlySearchResults } from "./AcademicScholarlySearchResults";
import { scholarlySearchPageInfo, scholarlySearchPageSize, toScholarlySearchRows } from "./academicScholarlySearch";
import { createAcademicTranslator } from "./academicStrings";
import { createLabelsFixture, discoveredPage, discoveredRecord } from "./academicTestHelpers";

const t = createAcademicTranslator("en");
const labels = createLabelsFixture();

/** Anything that would identify the service behind a record must never appear. */
const forbidden = /openalex|crossref|open\s+library|google\s+books|pubmed|semanticscholar|provider|model\s+name|api\s+key|token|credit|billing|\$\d/i;

function render(result: AcademicSourceSearchResponse, options: { known?: boolean; busy?: boolean } = {}) {
  const rows = toScholarlySearchRows(result.sources);
  const page = scholarlySearchPageInfo(result, rows);
  const html = renderToStaticMarkup(
    <AcademicScholarlySearchResults
      result={result}
      rows={rows}
      page={page}
      isKnown={() => options.known === true}
      busy={options.busy === true}
      t={t}
      labels={labels}
      onAdd={vi.fn()}
      onPage={vi.fn()}
    />,
  );
  const text = html
    .replace(/<[^>]+>/g, " ")
    .replace(/&#x27;|&apos;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&amp;/g, "&")
    .replace(/\s+/g, " ");
  return { html, text };
}

describe("metadata versus full text integrity", () => {
  it("labels a bibliographic record as a record", () => {
    const record = discoveredRecord({ accessLevel: "metadata", fullTextIndicated: false });
    const { text } = render(discoveredPage({ sources: [record] }));
    expect(text).toContain("Bibliographic record only: this service has not read the work.");
    expect(text).not.toContain("Full-text location indicated");
    expect(text).not.toMatch(/full[_ ]text[_ ]content/i);
  });

  it("shows a catalogue abstract as an abstract, not as the work", () => {
    const record = discoveredRecord({ accessLevel: "abstract", abstract: "Synthetic abstract sentence." });
    const { text } = render(discoveredPage({ sources: [record] }));
    expect(text).toContain("Catalogue abstract");
    expect(text).toContain("Synthetic abstract sentence.");
    expect(text).toContain("the full work has not been read by this service.");
  });

  it("separates an indicated full-text location from having read it", () => {
    const record = discoveredRecord({ accessLevel: "metadata", fullTextIndicated: true, fullTextUrl: "https://synth.example/full/1" });
    const { html, text } = render(discoveredPage({ sources: [record] }));
    expect(text).toContain("Full-text location indicated");
    expect(text).toContain("a public full-text location was pointed at, but this service has not read it.");
    // The access pill stays at the catalogue's own level; nothing claims `full_text`.
    expect(html).toContain("access:metadata");
    expect(html).not.toContain("access:full_text");
  });

  it("says the retraction status is still unchecked", () => {
    const { text } = render(discoveredPage());
    expect(text).toContain("Retraction status has not been checked yet.");
  });

  it("names the provenance as scholarly records plus a retrieval date", () => {
    const { text } = render(discoveredPage());
    expect(text).toContain("Found in scholarly records, retrieved 2026-11-24");
  });
});

describe("no service, model or cost detail reaches the page", () => {
  it("renders a full result set with nothing forbidden in it", () => {
    const result = discoveredPage({
      partial: true,
      totalMatched: 150,
      hasMore: true,
      doiChecks: [{ submittedDoi: "10.5555/synth.2024.0001", isRegistered: false, failureCode: "not_registered" }],
      sources: [
        discoveredRecord({ sourceType: "other", doi: null, isbn13: "9780306406157", accessLevel: "abstract", abstract: "A." }),
        discoveredRecord({ title: "Second synthetic record", landingUrl: null, fullTextIndicated: true }),
      ],
    });
    const { html, text } = render(result);
    expect(text).not.toMatch(forbidden);
    expect(html).not.toMatch(forbidden);
    expect(html).not.toContain("scholarly_catalog_metadata");
    expect(html).not.toMatch(/openalex\.org|crossref\.org|googleapis\.com/i);
  });

  it("reports an unregistered DOI without naming a registry", () => {
    const result = discoveredPage({ doiChecks: [{ submittedDoi: "10.5555/nope.0001", isRegistered: false, failureCode: "timeout" }] });
    const { text } = render(result);
    expect(text).toContain("The registration check for 10.5555/nope.0001 took too long and did not finish.");
    expect(text).not.toMatch(forbidden);
  });

  it("shows a registered DOI plainly", () => {
    const result = discoveredPage({ doiChecks: [{ submittedDoi: "10.5555/synth.2024.0001", isRegistered: true, failureCode: null }] });
    const { text } = render(result);
    expect(text).toContain("10.5555/synth.2024.0001 is a registered DOI.");
  });
});

describe("search result accounting", () => {
  it("counts what matched and what this page shows", () => {
    const { text } = render(discoveredPage({ totalMatched: 150, hasMore: true, page: 4 }));
    expect(text).toContain("150 record(s) surfaced by this search; 1 on this page");
    expect(text).toContain("This search stopped at one of its own limits");
    // The old sentence quoted the combined bound as if it were the reason. It was only ever one of
    // three ceilings, and quoting it taught students to read "150" as the size of the literature.
    expect(text).not.toContain("the bound is");
  });

  // The case the bound-based inference got wrong: a deployment whose one catalog came back with
  // exactly its per-catalog ceiling, far below the combined pool. The server's flag is what makes
  // the list honest here, because nothing about `totalMatched` alone could tell either way.
  it("says a search was cut off when a single catalog hit its own ceiling", () => {
    const pool = Array.from({ length: 50 }, (_, index) => discoveredRecord({ title: `Synthetic record ${index + 1}`, doi: `10.5555/synth.${index + 1}` }));
    const { text } = render(
      discoveredPage({ page: 1, totalMatched: 50, hasMore: true, sources: pool.slice(0, scholarlySearchPageSize) }),
    );
    expect(text).toContain("50 record(s) surfaced by this search; 10 on this page");
    expect(text).toContain("This search stopped at one of its own limits, so more records may match than are listed here.");
  });

  it("announces a partial result instead of implying completeness", () => {
    const { text } = render(discoveredPage({ partial: true }));
    expect(text).toContain("Part of the scholarly record pool did not answer");
  });

  it("explains an empty search as an empty search", () => {
    const { text } = render(discoveredPage({ sources: [], totalMatched: 0 }));
    expect(text).toContain("No scholarly record matched that search.");
    expect(text).not.toContain("surfaced by this search");
  });

  // A zero-row page used to bail out of rendering before the identifier checks, so an unregistered
  // DOI — the one thing this search had proved — vanished with the list. The check is the answer
  // the student asked for, not decoration on the list, so it has to survive an empty page.
  it("keeps identifier checks visible when the page has no records", () => {
    const result = discoveredPage({
      sources: [],
      totalMatched: 0,
      doiChecks: [
        { submittedDoi: "10.5555/unregistered.0001", isRegistered: false, failureCode: "not_registered" },
        { submittedDoi: "10.5555/synth.2024.0001", isRegistered: true, failureCode: null },
      ],
    });
    const { text } = render(result);
    expect(text).toContain("No scholarly record matched that search.");
    expect(text).toContain("10.5555/unregistered.0001 is not a registered DOI.");
    expect(text).toContain("10.5555/synth.2024.0001 is a registered DOI.");
    expect(text).not.toMatch(forbidden);
  });

  it("still offers the paging controls on an empty page", () => {
    const { html } = render(discoveredPage({ sources: [], totalMatched: 0 }));
    expect(html).toContain("Previous page");
    expect(html).toContain("Next page");
  });
});

describe("paging and adding controls", () => {
  function disabledLabels(html: string): string[] {
    return [...html.matchAll(/<button[^>]*disabled[^>]*>([\s\S]*?)<\/button>/g)].map((match) => match[1].replace(/<[^>]+>/g, "").trim());
  }

  // Page 1 of a pool larger than one page must offer Next even when the pool was not truncated:
  // the old page-sized fetch made totalMatched equal one page, so Next was unreachable here.
  it("enables Next from page 1 of a multi-page pool", () => {
    const pool = Array.from({ length: 24 }, (_, index) => discoveredRecord({ title: `Synthetic record ${index + 1}`, doi: `10.5555/synth.${index + 1}` }));
    const { html, text } = render(
      discoveredPage({ page: 1, totalMatched: pool.length, hasMore: false, sources: pool.slice(0, scholarlySearchPageSize) }),
    );
    expect(disabledLabels(html)).toContain("Previous page");
    expect(disabledLabels(html)).not.toContain("Next page");
    expect(text).toContain("24 record(s) surfaced by this search; 10 on this page");
    expect(text).not.toContain("This search stopped at one of its own limits");
  });

  it("starts with no previous page", () => {
    const { html } = render(discoveredPage({ page: 1, totalMatched: scholarlySearchPageSize * 15, hasMore: true }));
    expect(disabledLabels(html)).toContain("Previous page");
  });

  it("refuses to page past the bound the server enforces", () => {
    const { html } = render(discoveredPage({ page: 15, totalMatched: 150, hasMore: true }));
    expect(disabledLabels(html)).toContain("Next page");
  });

  it("offers both directions in the middle of a long result", () => {
    const { html } = render(discoveredPage({ page: 7, totalMatched: 150, hasMore: true }));
    expect(disabledLabels(html)).toEqual([]);
  });

  it("disables every action while a newer search is in flight", () => {
    const { html } = render(discoveredPage({ page: 2, totalMatched: 150, hasMore: true }), { busy: true });
    // Add, Previous, Next: three controls, and no enabled button in the paging bar.
    expect(disabledLabels(html).length).toBe(3);
  });

  it("marks a record already in the library instead of adding it twice", () => {
    const { html, text } = render(discoveredPage(), { known: true });
    expect(text).toContain("Already in your library");
    // The control is the disabled one, so a second click cannot duplicate the record.
    expect(disabledLabels(html)).toContain("Already in your library");
  });

  it("links the DOI resolver and the record landing page only", () => {
    const { html } = render(discoveredPage({ sources: [discoveredRecord({ doi: "10.5555/synth.2024.0001", landingUrl: "https://synth.example/records/1" })] }));
    expect(html).toContain('href="https://doi.org/10.5555/synth.2024.0001"');
    expect(html).toContain('href="https://synth.example/records/1"');
    expect(html).toContain('rel="noopener noreferrer"');
  });

  it("drops a non-http link rather than rendering it", () => {
    const { html } = render(discoveredPage({ sources: [discoveredRecord({ landingUrl: "javascript:alert(1)" })] }));
    expect(html).not.toContain("href=\"javascript:");
  });

  it("tells the reader that a page turn is another search, not a slice of this one", () => {
    // The panel keeps no cursor and caches no pool, so "next" issues a second search whose list may
    // differ from the first. Copy promising a stable set would describe an implementation this
    // feature does not have, so the caveat belongs where the reader is about to press the button.
    const { text } = render(discoveredPage({ page: 1, totalMatched: scholarlySearchPageSize * 15, hasMore: true }));
    expect(text).toMatch(/turning the page searches the catalogs again/i);
    expect(text).toMatch(/appear twice, or move/i);
    expect(text).not.toMatch(/same records on every page|stable (list|result|snapshot)|identical across pages/i);
  });
});

describe("machine-local catalog addresses never become clickable", () => {
  // Issue #1267 slice 3, round 9. The payloads behind these DTOs are the ones the API tests feed
  // through the real OpenAlex and Google Books adapters; what `AcademicSourcePresentation.ToDto`
  // returns for them is the contract the studio consumes, and this group pins both halves of it: an
  // address into somebody's own machine never reaches a reader as a link, and losing that address
  // costs the reader none of the scholarship.
  it("renders no open-link at all for a record whose advertised address was refused", () => {
    // Emitted for the OpenAlex work whose primary_location.landing_page_url was
    // http://openalex-mirror.test/W10000000001 and whose best_oa_location.pdf_url was
    // http://nas.local:9200/elastic.pdf: both fields null, indication gone with the link.
    const refused = discoveredRecord({
      title: "Pricing and reimbursement in the Kurdistan Region",
      containerTitle: "Kurdish Health Policy Review",
      doi: "10.5555/synth.2024.0001",
      landingUrl: null,
      fullTextUrl: null,
      fullTextIndicated: false,
    });
    const { html, text } = render(discoveredPage({ sources: [refused] }));
    expect(html).not.toContain("Open the record");
    expect(html).not.toContain("openalex-mirror.test");
    expect(html).not.toContain("nas.local");
    expect(html).not.toMatch(/href="https?:\/\/(127\.|0\.0\.0\.0|localhost|169\.254\.|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|\[::1\])/i);
    // The DOI resolver link is built from the identifier, not from provider text, so it stays.
    expect(html).toContain('href="https://doi.org/10.5555/synth.2024.0001"');
    expect(text).toContain("Pricing and reimbursement in the Kurdistan Region");
    expect(text).toContain("Kurdish Health Policy Review");
    expect(text).toMatch(/Bibliographic record/i);
    expect(text).not.toMatch(/full-text location/i);
  });

  it("renders the public journal address the record did advertise", () => {
    const publicRecord = discoveredRecord({
      title: "Essential medicines lists in fragile regulatory systems",
      doi: "10.1007/s13347-020-00405-x",
      landingUrl: "https://journal.example.org/articles/45",
      fullTextUrl: "https://journal.example.org/articles/45.pdf",
      fullTextIndicated: true,
    });
    const { html } = render(discoveredPage({ sources: [publicRecord] }));
    expect(html).toContain('href="https://journal.example.org/articles/45"');
    expect(html).toContain("Open the record");
    expect(html).toContain('rel="noopener noreferrer"');
    expect(html).toContain('target="_blank"');
  });

  it("still refuses a local address that reached the page without passing the server gate", () => {
    // Defense in depth for a contract violation rather than the normal path: the DTO is supposed to
    // carry null here. Even a stale or hand-built response cannot put these behind a button.
    ["http://db.internal/report", "http://127.0.0.1:8080/stolen", "http://localhost:9200/elastic", "http://[::1]/", "http://nas/admin", "http://2130706433/", "http://staging.intranet/docs"].forEach(url => {
      const { html } = render(discoveredPage({ sources: [discoveredRecord({ landingUrl: url, fullTextUrl: url })] }));
      expect(html).not.toContain("Open the record");
      expect(html).not.toContain(`href="${url}"`);
    });
  });

  it("keeps the bibliographic content when the link is refused", () => {
    const refused = discoveredRecord({
      title: "Reimbursement policy in emerging jurisdictions",
      containerTitle: "Health Policy and Planning",
      publicationYear: 2022,
      citationCount: 3,
      landingUrl: "http://router.home/setup",
      fullTextUrl: null,
    });
    const { text } = render(discoveredPage({ sources: [refused] }));
    expect(text).not.toContain("router.home");
    expect(text).toContain("Reimbursement policy in emerging jurisdictions");
    expect(text).toContain("Health Policy and Planning");
    expect(text).toContain("2022");
  });
});
