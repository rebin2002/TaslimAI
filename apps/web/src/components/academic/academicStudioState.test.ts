/**
 * Issue #1259 (Academic Research Studio) — Phase A workspace state tests.
 *
 * Covers the keyboard contract of the tab strip, the chapter skeleton behaviour
 * per study level, evidence-note bookkeeping and the privacy-scoped draft
 * round-trip. Synthetic stores only: nothing touches real browser storage.
 */
import { describe, expect, it } from "vitest";
import {
  academicTabs,
  chapterAllocationStatus,
  chapterTemplate,
  clearWorkspaceDraft,
  createChapterPlan,
  createInitialWorkspaceState,
  evidenceCoverage,
  evidenceNoteKey,
  nextTabFromKey,
  parseWorkspaceState,
  pruneEvidenceNotes,
  readWorkspaceDraft,
  resizeChapterPlan,
  setEvidenceNote,
  updateChapter,
  writeWorkspaceDraft,
} from "./academicStudioState";
import { createDefaultBrief } from "./academicBrief";
import { createMemoryStore, fixturePrincipal, otherPrincipal, verifiedSource } from "./academicTestHelpers";

describe("tab keyboard contract", () => {
  it("moves forward and backward through the four sections", () => {
    expect(nextTabFromKey("plan", "ArrowRight")).toBe("sources");
    expect(nextTabFromKey("sources", "ArrowRight")).toBe("evidence");
    expect(nextTabFromKey("chapters", "ArrowRight")).toBe("plan");
    expect(nextTabFromKey("plan", "ArrowLeft")).toBe("chapters");
  });

  it("mirrors arrow direction for right-to-left locales", () => {
    expect(nextTabFromKey("plan", "ArrowLeft", { rtl: true })).toBe("sources");
    expect(nextTabFromKey("sources", "ArrowRight", { rtl: true })).toBe("plan");
  });

  it("jumps to the ends and ignores unrelated keys", () => {
    expect(nextTabFromKey("evidence", "Home")).toBe("plan");
    expect(nextTabFromKey("evidence", "End")).toBe("chapters");
    expect(nextTabFromKey("plan", "Enter")).toBeNull();
    expect(nextTabFromKey("plan", "a")).toBeNull();
  });

  it("keeps the four sections the issue names, in order", () => {
    expect([...academicTabs]).toEqual(["plan", "sources", "evidence", "chapters"]);
  });
});

describe("chapter plan", () => {
  it("gives a doctoral plan more structural sections than a master's plan", () => {
    expect(chapterTemplate("doctoral").length).toBeGreaterThan(chapterTemplate("masters").length);
    expect(chapterTemplate("doctoral")).toContain("Original Contribution to Knowledge");
    expect(createChapterPlan("masters")).toHaveLength(chapterTemplate("masters").length);
  });

  it("keeps the student's own titles when the level changes", () => {
    const plan = updateChapter(createChapterPlan("masters"), "chapter-1", { title: "My own opening", fromTemplate: false });
    const resized = resizeChapterPlan(plan, "doctoral");
    expect(resized[0].title).toBe("My own opening");
    expect(resized).toHaveLength(chapterTemplate("doctoral").length);
  });

  it("re-adopts the template title when the section is still untouched", () => {
    const resized = resizeChapterPlan(createChapterPlan("masters"), "doctoral");
    expect(resized[1].title).toBe(chapterTemplate("doctoral")[1]);
  });

  it("compares the plan only against the limit the student recorded", () => {
    const plan = updateChapter(updateChapter(createChapterPlan("masters"), "chapter-1", { targetWords: 8000 }), "chapter-2", { targetWords: 9000 });
    expect(chapterAllocationStatus(plan, 15000)).toEqual({ kind: "over", difference: 2000 });
    expect(chapterAllocationStatus(plan, 30000).kind).toBe("under");
    expect(chapterAllocationStatus(createChapterPlan("masters"), 15000).kind).toBe("empty");
    expect(chapterAllocationStatus(plan, null).kind).toBe("unset");
  });
});

describe("evidence notes", () => {
  it("adds, replaces and removes notes by source and question", () => {
    const source = verifiedSource();
    let notes = setEvidenceNote({}, source.id, 0, "Notes from page 44");
    expect(notes[evidenceNoteKey(source.id, 0)]).toBe("Notes from page 44");
    notes = setEvidenceNote(notes, source.id, 0, "   ");
    expect(notes).toEqual({});
  });

  it("counts coverage without inventing empty cells", () => {
    const a = verifiedSource({ id: "src-a" });
    const b = verifiedSource({ id: "src-b" });
    const notes = setEvidenceNote(setEvidenceNote({}, a.id, 0, "answer"), b.id, 1, "answer");
    expect(evidenceCoverage([a, b], notes)).toEqual({ cells: 4, filled: 2, sourcesCovered: 2 });
    expect(evidenceCoverage([a, b], {}).cells).toBe(0);
  });

  it("prunes notes that reference a removed source", () => {
    const a = verifiedSource({ id: "src-a" });
    const b = verifiedSource({ id: "src-b" });
    const notes = setEvidenceNote(setEvidenceNote({}, a.id, 0, "keep"), b.id, 0, "drop");
    expect(pruneEvidenceNotes(notes, [a])).toEqual({ [evidenceNoteKey(a.id, 0)]: "keep" });
  });
});

describe("draft persistence", () => {
  it("round-trips the workspace through the scoped store", () => {
    const store = createMemoryStore();
    const state = {
      ...createInitialWorkspaceState("doctoral"),
      brief: { ...createDefaultBrief("doctoral"), researchQuestion: "How does synthetic policy shape synthetic access?" },
      sources: [verifiedSource()],
      evidenceNotes: { [evidenceNoteKey("src-fixture-1", 0)]: "answer" },
      tab: "sources" as const,
    };
    expect(writeWorkspaceDraft(state, fixturePrincipal, store)).toBe(true);
    const restored = readWorkspaceDraft(fixturePrincipal, store);
    expect(restored?.brief.researchQuestion).toBe(state.brief.researchQuestion);
    expect(restored?.tab).toBe("sources");
    expect(restored?.chapters).toHaveLength(chapterTemplate("doctoral").length);
    expect(restored?.sources.map((source) => source.id)).toEqual(["src-fixture-1"]);

    expect(clearWorkspaceDraft(fixturePrincipal, store)).toBe(true);
    expect(readWorkspaceDraft(fixturePrincipal, store)).toBeNull();
  });

  it("refuses to restore another identity's draft and removes it", () => {
    const store = createMemoryStore();
    writeWorkspaceDraft({ ...createInitialWorkspaceState(), brief: { ...createDefaultBrief(), researchQuestion: "Private question belonging to another student" } }, otherPrincipal, store);
    expect(readWorkspaceDraft(fixturePrincipal, store)).toBeNull();
    // The other identity's own entry is untouched: the keys are per-principal.
    expect(readWorkspaceDraft(otherPrincipal, store)).not.toBeNull();
  });

  it("rejects an anonymous principal rather than writing a shared draft", () => {
    const store = createMemoryStore();
    expect(writeWorkspaceDraft(createInitialWorkspaceState(), { userId: null, workspaceId: null }, store)).toBe(false);
    expect(store.length).toBe(0);
  });

  it("survives a blocked storage layer instead of throwing", () => {
    expect(writeWorkspaceDraft(createInitialWorkspaceState(), fixturePrincipal, null)).toBe(false);
    expect(readWorkspaceDraft(fixturePrincipal, null)).toBeNull();
  });
});

describe("parseWorkspaceState", () => {
  it("accepts a well-formed payload", () => {
    const parsed = parseWorkspaceState({
      brief: { ...createDefaultBrief(), researchQuestion: "x" },
      tab: "evidence",
      chapters: [{ id: "chapter-1", title: "Introduction", status: "writing", targetWords: 1000, notes: "", fromTemplate: true }],
      evidenceNotes: { "src-a::0": "note" },
      sources: [verifiedSource()],
    });
    expect(parsed?.tab).toBe("evidence");
    expect(parsed?.chapters[0].status).toBe("writing");
    expect(parsed?.sources[0].title).toBe("Synthetic regulation of synthetic medicines");
  });

  it("drops malformed fields rather than trusting them", () => {
    expect(parseWorkspaceState(null)).toBeNull();
    expect(parseWorkspaceState("nope")).toBeNull();
    expect(parseWorkspaceState({ brief: { researchQuestion: 42 } })).toBeNull();
    const partial = parseWorkspaceState({ brief: { researchQuestion: "ok", level: "doctoral" }, tab: "nonsense", chapters: "no", evidenceNotes: 3, sources: [{ nope: true }] });
    expect(partial?.tab).toBe("plan");
    expect(partial?.chapters).toEqual([]);
    expect(partial?.evidenceNotes).toEqual({});
    expect(partial?.sources).toEqual([]);
  });

  it("normalises an unrecognised access level to the conservative default", () => {
    const parsed = parseWorkspaceState({
      brief: { ...createDefaultBrief(), researchQuestion: "ok" },
      sources: [{ ...verifiedSource(), access: "definitely_read", retractionStatus: "banana", authors: "not-an-array" }],
    });
    expect(parsed?.sources[0].access).toBe("metadata");
    expect(parsed?.sources[0].retractionStatus).toBe("unknown");
    expect(parsed?.sources[0].authors).toEqual([]);
  });
});
