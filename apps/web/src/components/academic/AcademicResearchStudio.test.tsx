/**
 * Issue #1259 (Academic Research Studio) — composition, honesty and privacy tests.
 *
 * Renders the whole Phase A surface with the repo's `react-dom/server`
 * convention and provider mocks, using synthetic identities only. The point of
 * these assertions is the product's promise: the academic entry is real, the
 * standard entry is untouched, and nothing on the page implies a capability the
 * backend does not have.
 */
import { describe, expect, it, vi } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";

const harness = vi.hoisted(() => ({ user: { id: "user-synth-1" }, workspace: { id: "ws-synth-1" } }));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: harness.user, workspace: harness.workspace, loading: false }),
}));

// The studio must not depend on the shared dictionary for its own copy, so the
// mock returns the key: any leaked i18n key becomes visible in the assertions.
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ t: (key: string) => key, locale: "en" }),
}));

import { AcademicResearchStudio } from "./AcademicResearchStudio";
import { createInitialWorkspaceState } from "./academicStudioState";
import { createDefaultBrief } from "./academicBrief";
import { verifiedSource } from "./academicTestHelpers";
import type { AcademicWorkspaceState } from "./academicStudioState";

/** Static markup -> visible text, with the entity escaping React applies undone. */
function toText(html: string): string {
  return html
    .replace(/<[^>]+>/g, " ")
    .replace(/&#x27;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&amp;/g, "&")
    .replace(/\s+/g, " ");
}

function renderTab(state?: Partial<AcademicWorkspaceState>) {
  const initialState: AcademicWorkspaceState = { ...createInitialWorkspaceState("masters"), ...state };
  return renderToStaticMarkup(<AcademicResearchStudio initialState={initialState} />);
}

const rendered = renderTab();
const text = toText(rendered);

describe("AcademicResearchStudio header and entries", () => {
  it("renders one h1 naming the academic surface", () => {
    expect((rendered.match(/<h1/g) ?? [])).toHaveLength(1);
    expect(rendered).toContain("Academic Research Studio");
  });

  it("offers both research modes and keeps standard research reachable", () => {
    expect(rendered).toContain('href="/create/research"');
    expect(text).toContain("Standard research");
    expect(text).toContain("Academic research");
    expect(rendered).toContain('aria-current="page"');
  });

  it("states the privacy position up front", () => {
    expect(text).toContain("stay in your workspace");
  });
});

describe("AcademicResearchStudio brief form", () => {
  it("renders the level, discipline, style and methodology controls", () => {
    expect(text).toContain("Study level");
    expect(text).toContain("Master's");
    expect(text).toContain("Doctoral");
    expect(text).toContain("Discipline");
    expect(text).toContain("Citation style");
    expect(text).toContain("Methodology");
    expect(text).toContain("University requirements");
  });

  it("offers the five citation styles named in the issue", () => {
    for (const style of ["APA 7th", "Harvard", "Chicago", "IEEE", "Vancouver"]) expect(rendered).toContain(style);
  });

  it("lists scholarly source types rather than website-only sources", () => {
    for (const type of ["Journal article", "Book", "Thesis or dissertation", "Conference paper", "Report or grey literature"]) {
      expect(rendered, type).toContain(type);
    }
  });

  it("labels every text control", () => {
    const labels = rendered.match(/<label[^>]*for="[^"]+"/g) ?? [];
    expect(labels.length).toBeGreaterThan(8);
    // Each `for=` must point at a control id that is actually rendered.
    for (const match of labels) {
      const id = /for="([^"]+)"/.exec(match)?.[1];
      expect(id, match).toBeTruthy();
      expect(rendered, `missing control for ${id}`).toContain(`id="${id}"`);
    }
  });

  it("marks the required fields for assistive technology", () => {
    expect(rendered).toContain('aria-required="true"');
  });

  it("never relies on a placeholder as the only label", () => {
    // Every control needs a real accessible name: an explicit <label for>, an
    // implicit wrapping <label>, or aria-label / aria-labelledby.
    const ranges: [number, number][] = [];
    const open = /<label\b[^>]*>/g;
    const close = /<\/label>/g;
    let found = open.exec(rendered);
    while (found) {
      close.lastIndex = found.index;
      const end = close.exec(rendered);
      if (end) ranges.push([found.index, end.index]);
      found = open.exec(rendered);
    }
    const unlabelled = [...rendered.matchAll(/<input[^>]*>/g)]
      .filter(({ index, 0: tag }) => {
        if (/aria-label/.test(tag)) return false;
        const id = /id="([^"]+)"/.exec(tag)?.[1];
        if (id && rendered.includes(`for="${id}"`)) return false;
        return !ranges.some(([start, stop]) => index > start && index < stop);
      })
      .map((match) => match[0]);
    expect(unlabelled).toEqual([]);
  });
});

describe("AcademicResearchStudio tabs", () => {
  it("renders the four sections the issue names", () => {
    expect(rendered).toContain('role="tablist"');
    for (const tab of ["Plan", "Sources", "Evidence", "Chapters"]) expect(rendered).toContain(`>${tab}<`);
  });

  it("opens on the Plan section with the staged workflow visible", () => {
    expect(rendered).toContain('aria-selected="true"');
    expect(text).toContain("Research plan");
    expect(text).toContain("Scholarly discovery");
    expect(text).toContain("Verification and delivery");
  });

  it("wires every tab to its own panel", () => {
    expect(toText(renderTab({ tab: "sources" }))).toContain("Record what you have verified");
    expect(toText(renderTab({ tab: "evidence" }))).toContain("Add sources first");
    expect(toText(renderTab({ tab: "chapters" }))).toContain("Word allocation");
  });
});

describe("AcademicResearchStudio honesty", () => {
  it("labels later-phase capabilities instead of offering controls for them", () => {
    expect(text).toContain("Coming in a later step");
    expect(text).toContain("Starting an academic run is not available yet");
    const evidenceState: Partial<AcademicWorkspaceState> = {
      tab: "evidence",
      sources: [verifiedSource()],
      brief: { ...createDefaultBrief("masters"), reviewQuestions: ["Which rule applies?"] },
    };
    expect(toText(renderTab(evidenceState))).toContain("Manual extraction is available; automated extraction is not");
    expect(toText(renderTab({ tab: "chapters" }))).toContain("Planning only — no text is generated");
  });

  it("renders no control that would start a job, generate text or export a document", () => {
    const allowed = new Set([
      "Plan",
      "Sources",
      "Evidence",
      "Chapters",
      "Save draft",
      "Start over",
      "Use common practice for this discipline",
      "Add another question",
      "Remove",
      "Needs attention",
      "Optional",
      "Add to library",
      "Clear form",
    ]);
    for (const tab of ["plan", "sources", "evidence", "chapters"] as const) {
      const html = renderTab({ tab });
      const buttons = (html.match(/<button[^>]*>([\s\S]*?)<\/button>/g) ?? [])
        // Tab buttons carry a live count ("Sources 3"), which is not part of the label.
        .map((tag) => toText(tag.replace(/<[^>]+>/g, " ")).trim().replace(/\s+\d+$/, ""));
      for (const button of buttons) {
        expect([...allowed, ""], `unexpected button on ${tab}: ${button}`).toContain(button);
      }
      expect(toText(html), tab).not.toMatch(/generate chapter|run academic|start research|export (docx|pdf)|begin drafting/i);
    }
  });

  it("never exposes provider, model or cost information", () => {
    for (const tab of ["plan", "sources", "evidence", "chapters"] as const) {
      expect(toText(renderTab({ tab })).toLowerCase(), tab).not.toMatch(
        /openalex|crossref|google books|open library|pubmed|gpt|claude|gemini|llama|provider|model name|api key|tokens|credits|\$\d/,
      );
    }
  });

  it("does not render a raw translation key", () => {
    expect(text).not.toMatch(/\b(tab|page|field|sources|plan|evidence|chapters|mode|status|action|error|warning|stage|capability)\.[a-zA-Z]/);
  });

  it("says plainly that the plan is reviewed before anything runs", () => {
    expect(text).toContain("Nothing runs yet");
  });
});

describe("AcademicResearchStudio with a filled brief", () => {
  const filledState: AcademicWorkspaceState = {
    ...createInitialWorkspaceState("doctoral"),
    brief: {
      ...createDefaultBrief("doctoral"),
      discipline: "pharmacy_and_pharmacotherapy",
      citationStyle: "vancouver",
      methodologies: ["systematic_review"],
      researchQuestion: "How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq since 2012?",
      reviewQuestions: ["Which pricing rule applies to essential medicines?"],
      geographicScope: "Kurdistan Region of Iraq",
      requirements: { wordCount: 40000, minimumSources: 60, minimumPeerReviewed: 40, allowedSourceTypes: ["journal_article", "thesis", "report"], requireFullText: true, referencingGuideNotes: "" },
    },
    sources: [verifiedSource()],
  };
  const filledText = toText(renderTab({ ...filledState, tab: "plan" }));

  it("echoes the student's own brief back to them in the summary", () => {
    expect(filledText).toContain("Kurdistan Region of Iraq");
    expect(filledText).toContain("How has pharmaceutical market access regulation evolved");
    expect(filledText).toContain("40,000");
    expect(filledText).toContain("Which pricing rule applies to essential medicines?");
  });

  it("counts the recorded library against the plan", () => {
    expect(filledText).toContain("Sources: 1");
    expect(filledText).toContain("you have recorded 1");
  });

  it("shows the doctoral chapter skeleton", () => {
    // Section titles live in input values, so the markup is checked directly.
    const chaptersHtml = renderTab({ ...filledState, tab: "chapters" });
    expect(chaptersHtml).toContain('value="Original Contribution to Knowledge"');
    expect(chaptersHtml).toContain('value="Theoretical Framework"');
    expect(chaptersHtml).toContain("Chapter plan — Doctoral");
  });

  it("lists the verified source with its own identifier on the Sources tab", () => {
    const sourcesHtml = renderTab({ ...filledState, tab: "sources" });
    expect(sourcesHtml).toContain("Synthetic regulation of synthetic medicines");
    expect(sourcesHtml).toContain('href="https://doi.org/10.5555/synth.2023.0008"');
  });

  it("reports the full-text requirement honestly", () => {
    expect(filledText).toContain("only works you can read in full");
  });
});
