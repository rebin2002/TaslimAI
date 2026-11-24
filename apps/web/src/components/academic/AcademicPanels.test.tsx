/**
 * Issue #1259 (Academic Research Studio) — panel behaviour tests.
 *
 * Synthetic records and `react-dom/server` rendering. What is pinned here is the
 * part a screenshot would miss: that a citation is shown with the identifier the
 * student supplied, that a missing element is announced rather than papered over,
 * and that an empty section explains the next real step instead of showing a
 * spinner for a job that cannot run.
 *
 * Issue #1267 slice 1 adds the scholarly search surface to this panel. A static
 * render can only ever be the idle state — a search needs a student to start one
 * — so the result rendering is pinned in `AcademicScholarlySearchResults.test.tsx`
 * and the request lifecycle in `academicScholarlySearch.test.ts`.
 */
import { describe, expect, it, vi } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { AcademicSourcesPanel } from "./AcademicSourcesPanel";
import { AcademicEvidencePanel } from "./AcademicEvidencePanel";
import { AcademicChaptersPanel } from "./AcademicChaptersPanel";
import { AcademicPlanPanel } from "./AcademicPlanPanel";
import { createChapterPlan, evidenceNoteKey } from "./academicStudioState";
import { createDefaultBrief, validateAcademicBrief } from "./academicBrief";
import { createAcademicTranslator } from "./academicStrings";
import { createLabelsFixture, verifiedSource } from "./academicTestHelpers";

const t = createAcademicTranslator("en");
const labels = createLabelsFixture();

describe("AcademicSourcesPanel", () => {
  function renderSources(sources: Parameters<typeof AcademicSourcesPanel>[0]["sources"], workspaceId: string | null = "ws-synth-1") {
    return renderToStaticMarkup(
      <AcademicSourcesPanel
        sources={sources}
        onLibraryChange={vi.fn()}
        citationStyle="apa7"
        allowedSourceTypes={["journal_article", "book", "thesis"]}
        requireFullText={false}
        t={t}
        labels={labels}
        workspaceId={workspaceId}
      />,
    );
  }

  it("explains what live search does and that manual entry still counts", () => {
    const html = renderSources([]);
    expect(html).toContain("Find records, then verify them");
    expect(html).toContain("It does not read the works");
    expect(html).toContain("No sources yet");
    // Discovery is live, but nothing here starts a job or generates text.
    expect(html).not.toMatch(/start academic run|generate chapter|export docx/i);
  });

  it("offers a real search form once the panel has a workspace", () => {
    const html = renderSources([], "ws-synth-1");
    expect(html).toContain("Find scholarly sources");
    expect(html).toContain("<form");
    expect(html).toContain('aria-busy="false"');
    expect(html).toContain(">Search</button>");
    expect(html).toContain("No search yet. Records appear here once you search.");
    // Paging, stop and retry belong to a search that has not happened yet.
    expect(html).not.toContain("Previous page");
    expect(html).not.toContain(">Stop</button>");
    expect(html).not.toContain(">Try again</button>");
  });

  it("does not show a search control it cannot honour without a workspace", () => {
    const html = renderSources([], null);
    expect(html).toContain("Scholarly search needs a workspace");
    expect(html).toContain("Record search is scoped to one workspace and this view has none.");
    expect(html).not.toContain("Find scholarly sources");
    expect(html).not.toContain(">Search</button>");
    // Manual entry is the honest alternative and stays reachable.
    expect(html).toContain("Add a verified source");
  });

  it("never names the service behind a record in the panel markup", () => {
    const html = renderSources([verifiedSource()]).toLowerCase();
    expect(html).not.toMatch(/openalex|crossref|open library|google books|pubmed|provider|api key|tokens|credits/);
  });

  it("shows the verified record with its DOI resolver link", () => {
    const html = renderSources([verifiedSource()]);
    expect(html).toContain("Synthetic regulation of synthetic medicines");
    expect(html).toContain('href="https://doi.org/10.5555/synth.2023.0008"');
    expect(html).toContain('rel="noopener noreferrer"');
    expect(html).toContain("Khoshnaw, R.");
    expect(html).toContain(">S1<");
  });

  it("labels the record's provenance rather than implying a check it did not run", () => {
    const html = renderSources([verifiedSource({ retractionStatus: "unknown", peerReviewed: true })]);
    expect(html).toContain("Retraction status has not been checked yet.");
  });

  it("lists missing bibliographic elements instead of guessing them", () => {
    const html = renderSources([verifiedSource({ pages: null, containerTitle: null, doi: null, url: null, isbn: null })]);
    expect(html).toContain("Missing from this record:");
    expect(html).toContain("journal or host book");
    expect(html).toContain("DOI, ISBN or link");
  });

  it("warns about a record with no identifier at all", () => {
    const html = renderSources([verifiedSource({ doi: null, isbn: null, url: null })]);
    expect(html).toContain("No verified identifier or link");
  });

  it("exposes the source-type and full-text filters", () => {
    const html = renderSources([]);
    expect(html).toContain('aria-label="Filter by source type"');
    expect(html).toContain("Full text read only");
    expect(html).toContain("Peer-reviewed only");
    expect(html).toContain('type="search"');
    // Every catalogue source type is offered, including scholarly ones the
    // generic web research list does not carry.
    for (const type of ["journal_article", "book", "book_chapter", "edited_volume", "conference_paper", "thesis", "report", "dataset", "working_paper", "standard_or_guideline", "legislation", "other"]) {
      expect(html, type).toContain(`sourceType:${type}`);
    }
    // The programme's own allowed list is echoed as advice, not enforcement.
    expect(html).toContain("Your plan allows: sourceType:journal_article, sourceType:book, sourceType:thesis");
  });

  it("keeps the entry form labelled and never auto-fills a citation element", () => {
    const html = renderSources([]);
    expect(html).toContain("Add a verified source");
    expect(html).toContain("Enter the details exactly as the work states them");
    // An empty form has no fabricated page range.
    expect(html).not.toMatch(/pp\. \d+-\d+/);
  });
});

describe("AcademicEvidencePanel", () => {
  const sources = [verifiedSource({ id: "src-a" }), verifiedSource({ id: "src-b", access: "abstract", title: "Second synthetic work" })];

  it("asks for sources when the library is empty", () => {
    const html = renderToStaticMarkup(
      <AcademicEvidencePanel sources={[]} questions={["What changed?"]} notes={{}} onNoteChange={vi.fn()} t={t} labels={labels} />,
    );
    expect(html).toContain("Add sources first");
  });

  it("asks for sub-questions before building a matrix", () => {
    const html = renderToStaticMarkup(
      <AcademicEvidencePanel sources={sources} questions={["  "]} notes={{}} onNoteChange={vi.fn()} t={t} labels={labels} />,
    );
    expect(html).toContain("Define your sub-questions");
  });

  it("renders an accessible matrix keyed to the student's questions", () => {
    const html = renderToStaticMarkup(
      <AcademicEvidencePanel
        sources={sources}
        questions={["Which rule applies?", "What is the evidence strength?"]}
        notes={{ [evidenceNoteKey("src-a", 0)]: "Fee schedule 2019" }}
        onNoteChange={vi.fn()}
        t={t}
        labels={labels}
      />,
    );
    expect(html).toContain("<table");
    expect(html).toContain("<caption");
    expect(html).toContain('scope="col"');
    expect(html).toContain('scope="row"');
    expect(html).toContain("Which rule applies?");
    expect(html).toContain("Fee schedule 2019");
    expect((html.match(/<textarea/g) ?? [])).toHaveLength(4);
    expect(html).toContain("1 note(s) across 1 of 2 source(s)");
  });

  it("cautions against quoting pages for a work that is not fully read", () => {
    const html = renderToStaticMarkup(
      <AcademicEvidencePanel sources={sources} questions={["Which rule applies?"]} notes={{}} onNoteChange={vi.fn()} t={t} labels={labels} />,
    );
    expect(html).toContain("do not quote pages you cannot see");
  });

  it("states that automated extraction has not shipped", () => {
    const html = renderToStaticMarkup(
      <AcademicEvidencePanel sources={sources} questions={["Which rule applies?"]} notes={{}} onNoteChange={vi.fn()} t={t} labels={labels} />,
    );
    expect(html).toContain("Manual extraction is available; automated extraction is not");
  });
});

describe("AcademicChaptersPanel", () => {
  it("renders the level's skeleton with editable titles and statuses", () => {
    const html = renderToStaticMarkup(
      <AcademicChaptersPanel chapters={createChapterPlan("doctoral")} level="doctoral" wordCountLimit={40000} onChange={vi.fn()} t={t} />,
    );
    expect(html).toContain("Original Contribution to Knowledge");
    expect(html).toContain('value="Literature Review"');
    expect(html).toContain("Not started");
    expect(html).toContain("Chapter plan — Doctoral");
  });

  it("compares the plan against the student's own limit", () => {
    const chapters = createChapterPlan("masters");
    const html = renderToStaticMarkup(
      <AcademicChaptersPanel
        chapters={chapters.map((chapter, index) => ({ ...chapter, targetWords: index < 2 ? 12000 : 5000 }))}
        level="masters"
        wordCountLimit={30000}
        onChange={vi.fn()}
        t={t}
      />,
    );
    // 2 x 12,000 + 4 x 5,000 = 44,000 against a 30,000-word limit.
    expect(html).toContain("14,000 words more than the 30,000-word limit");
  });

  it("says nothing is generated and offers no export control", () => {
    const html = renderToStaticMarkup(<AcademicChaptersPanel chapters={createChapterPlan("masters")} level="masters" wordCountLimit={null} onChange={vi.fn()} t={t} />);
    expect(html).toContain("Planning only — no text is generated");
    expect(html).toContain("Record your word limit in the brief");
    expect(html).not.toMatch(/<button[^>]*>.*(export|download|generate)/i);
  });
});

describe("AcademicPlanPanel", () => {
  it("shows what is blocking the plan and links each item to its field", () => {
    const brief = createDefaultBrief("masters");
    const issues = validateAcademicBrief(brief);
    const html = renderToStaticMarkup(
      <AcademicPlanPanel brief={brief} issues={issues} sourceCount={0} fullTextCount={0} peerReviewedCount={0} t={t} labels={labels} onJumpToField={vi.fn()} />,
    );
    expect(html).toContain("Write the research question your submission answers.");
    expect(html).toContain("Select at least one methodology.");
    expect(html).toContain("2 item(s) need attention");
    expect((html.match(/role="alert"/g) ?? []).length).toBe(0);
  });

  it("summarises a complete plan and counts the stages running today", () => {
    const brief = {
      ...createDefaultBrief("doctoral"),
      discipline: "pharmacy_and_pharmacotherapy" as const,
      methodologies: ["systematic_review" as const],
      researchQuestion: "How has market access regulation evolved in the Kurdistan Region of Iraq?",
      reviewQuestions: ["Which rule applies?"],
      keywords: ["market access"],
      requirements: { wordCount: 40000, minimumSources: 60, minimumPeerReviewed: 40, allowedSourceTypes: ["journal_article" as const], requireFullText: true, referencingGuideNotes: "" },
    };
    const html = renderToStaticMarkup(
      <AcademicPlanPanel brief={brief} issues={validateAcademicBrief(brief)} sourceCount={12} fullTextCount={5} peerReviewedCount={10} t={t} labels={labels} onJumpToField={vi.fn()} />,
    );
    expect(html).toContain("Nothing is blocking this plan");
    // #1267 slice 1 ships scholarly discovery as a standalone capability, but the
    // discovery *stage* of the staged job has no wired runner: only the plan stage
    // executes today. The count must not rise until the academic job slice lands.
    expect(html).toContain("1 of 8 stages run today");
    // Integrity regression: exactly one stage (the plan li and its badge) renders
    // as available; the newly available discovery capability must not leak a
    // second runnable stage into the workflow surface.
    expect((html.match(/data-status="available"/g) ?? []).length).toBe(2);
    expect(html).toContain('data-status="planned"');
    expect(html).toContain("40,000");
    expect(html).toContain("you have recorded 12");
    expect(html).toContain("5 of 12 sources are marked as fully read");
  });
});
