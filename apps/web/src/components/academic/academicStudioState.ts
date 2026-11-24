/**
 * Issue #1259 (Academic Research Studio) — Phase A workspace state.
 *
 * Holds the review-only state the academic surface owns: the four tabs, their
 * keyboard contract, the chapter plan skeleton, per-source evidence notes and
 * identity-scoped draft persistence. Nothing here calls an API, so the tab
 * shell cannot offer a control whose backend does not exist yet.
 *
 * Drafts go through `sessionDraftScope`, the repo's privacy-scoped storage
 * primitive: an entry is keyed by BOTH user and workspace and a foreign or
 * malformed entry is dropped rather than restored, so switching accounts in the
 * same tab cannot expose another student's brief.
 */
import {
  clearSessionDraft,
  defaultSessionDraftStore,
  readSessionDraft,
  writeSessionDraft,
  type DraftPrincipal,
  type SessionDraftStore,
} from "@/lib/sessionDraftScope";
import { accessLevels, type AccessLevel, type AcademicSource } from "./academicSourceLibrary";
import { createDefaultBrief, type AcademicBrief, type AcademicLevel } from "./academicBrief";

export const academicTabs = ["plan", "sources", "evidence", "chapters"] as const;
export type AcademicTab = (typeof academicTabs)[number];

export const defaultAcademicTab: AcademicTab = "plan";

export const draftNamespace = "academic-research-brief";

/**
 * Roving-tabindex navigation for a horizontal tablist. Mirrors the WAI-ARIA
 * Authoring Practice Guide: Left/Right move between tabs (direction-aware for
 * RTL locales), Home/End jump to the ends, and the focus follows selection.
 * Returns null when the key is not a tablist binding, so the caller leaves it
 * to the browser.
 */
export function nextTabFromKey(current: AcademicTab, key: string, options: { rtl?: boolean } = {}): AcademicTab | null {
  const index = academicTabs.indexOf(current);
  if (index < 0) return null;
  const forward = options.rtl ? "ArrowLeft" : "ArrowRight";
  const backward = options.rtl ? "ArrowRight" : "ArrowLeft";
  if (key === forward) return academicTabs[(index + 1) % academicTabs.length];
  if (key === backward) return academicTabs[(index - 1 + academicTabs.length) % academicTabs.length];
  if (key === "Home") return academicTabs[0];
  if (key === "End") return academicTabs[academicTabs.length - 1];
  return null;
}

export type ChapterStatus = "not_started" | "outlined" | "writing" | "reviewing" | "complete";

export const chapterStatuses: readonly ChapterStatus[] = ["not_started", "outlined", "writing", "reviewing", "complete"];

export type PlannedChapter = {
  id: string;
  title: string;
  status: ChapterStatus;
  targetWords: number | null;
  notes: string;
  /** True when the section comes from the level template rather than the student. */
  fromTemplate: boolean;
};

/**
 * Chapter skeletons. These are the section names a programme's structure
 * commonly uses; the student renames them, and no content is generated here
 * (chapter drafting is a later phase).
 */
export const mastersChapterTemplate: readonly string[] = [
  "Introduction",
  "Literature Review",
  "Methodology",
  "Findings / Results",
  "Discussion",
  "Conclusion and Recommendations",
];

export const doctoralChapterTemplate: readonly string[] = [
  "Introduction",
  "Literature Review",
  "Theoretical Framework",
  "Research Methodology",
  "Data Analysis and Findings",
  "Discussion",
  "Original Contribution to Knowledge",
  "Conclusion and Future Research",
];

export function chapterTemplate(level: AcademicLevel): readonly string[] {
  return level === "doctoral" ? doctoralChapterTemplate : mastersChapterTemplate;
}

export function createChapterPlan(level: AcademicLevel): PlannedChapter[] {
  return chapterTemplate(level).map((title, index) => ({
    id: `chapter-${index + 1}`,
    title,
    status: "not_started",
    targetWords: null,
    notes: "",
    fromTemplate: true,
  }));
}

/** Rebuilds the skeleton when the level changes, keeping student edits by index. */
export function resizeChapterPlan(existing: readonly PlannedChapter[], level: AcademicLevel): PlannedChapter[] {
  const template = chapterTemplate(level);
  return template.map((title, index) => {
    const chapter = existing[index];
    if (chapter && chapter.fromTemplate) return { ...chapter, title };
    if (chapter) return chapter;
    return { id: `chapter-${index + 1}`, title, status: "not_started", targetWords: null, notes: "", fromTemplate: true };
  });
}

export function updateChapter(plan: readonly PlannedChapter[], id: string, patch: Partial<Omit<PlannedChapter, "id">>): PlannedChapter[] {
  return plan.map((chapter) => (chapter.id === id ? { ...chapter, ...patch } : chapter));
}

export function chapterWordAllocation(plan: readonly PlannedChapter[]): number {
  return plan.reduce((total, chapter) => total + (chapter.targetWords ?? 0), 0);
}

/**
 * Allocation advice only: compares the student's own targets with the
 * programme's own limit. It never states a rule the student did not enter.
 */
export function chapterAllocationStatus(
  plan: readonly PlannedChapter[],
  wordCountLimit: number | null,
): { kind: "empty" | "under" | "over" | "unset"; difference: number } {
  const allocated = chapterWordAllocation(plan);
  if (allocated === 0) return { kind: "empty", difference: 0 };
  if (wordCountLimit === null) return { kind: "unset", difference: allocated };
  return { kind: allocated > wordCountLimit ? "over" : "under", difference: allocated - wordCountLimit };
}

/** One cell of the evidence matrix: the student's own note for one question. */
export type EvidenceNote = { sourceId: string; questionIndex: number; text: string };

export function evidenceNoteKey(sourceId: string, questionIndex: number): string {
  return `${sourceId}::${questionIndex}`;
}

export function setEvidenceNote(
  notes: Readonly<Record<string, string>>,
  sourceId: string,
  questionIndex: number,
  text: string,
): Record<string, string> {
  const next = { ...notes };
  const key = evidenceNoteKey(sourceId, questionIndex);
  if (text.trim().length === 0) delete next[key];
  else next[key] = text;
  return next;
}

export function evidenceCoverage(sources: readonly AcademicSource[], notes: Readonly<Record<string, string>>): {
  cells: number;
  filled: number;
  sourcesCovered: number;
} {
  const keys = Object.keys(notes).filter((key) => (notes[key] ?? "").trim().length > 0);
  const covered = new Set(keys.map((key) => key.split("::")[0]));
  const questionCount = keys.reduce((max, key) => Math.max(max, Number(key.split("::")[1]) + 1), 0);
  return {
    cells: sources.length * questionCount,
    filled: keys.length,
    sourcesCovered: sources.filter((source) => covered.has(source.id)).length,
  };
}

/**
 * A note may only cite pages or quotations the student typed. This guard exists
 * because the integrity rule is about the *stored* record, not just the form.
 */
export function noteNeedsPageCaution(access: AccessLevel): boolean {
  return access !== "full_text";
}

export type AcademicWorkspaceState = {
  brief: AcademicBrief;
  tab: AcademicTab;
  chapters: PlannedChapter[];
  evidenceNotes: Record<string, string>;
  /** Verified sources the student recorded; part of the draft, not the server. */
  sources: AcademicSource[];
};

export function createInitialWorkspaceState(level: AcademicLevel = "masters"): AcademicWorkspaceState {
  return {
    brief: createDefaultBrief(level),
    tab: defaultAcademicTab,
    chapters: createChapterPlan(level),
    evidenceNotes: {},
    sources: [],
  };
}

/**
 * Normalises untrusted stored JSON before it reaches React. Anything malformed
 * restores as nothing at all rather than a partially trusted object.
 */
export function parseWorkspaceState(value: unknown): AcademicWorkspaceState | null {
  if (!value || typeof value !== "object") return null;
  const candidate = value as Partial<AcademicWorkspaceState>;
  if (!candidate.brief || typeof candidate.brief !== "object") return null;
  const brief = candidate.brief as Partial<AcademicBrief>;
  if (typeof brief.researchQuestion !== "string") return null;
  const base = createDefaultBrief(brief.level === "doctoral" ? "doctoral" : "masters");
  const restored: AcademicWorkspaceState = {
    brief: {
      ...base,
      ...brief,
      requirements: { ...base.requirements, ...(brief.requirements ?? {}) },
      methodologies: Array.isArray(brief.methodologies) ? brief.methodologies.filter((m): m is AcademicBrief["methodologies"][number] => typeof m === "string") : [],
      keywords: Array.isArray(brief.keywords) ? brief.keywords.filter((k): k is string => typeof k === "string") : [],
      reviewQuestions: Array.isArray(brief.reviewQuestions) ? brief.reviewQuestions.filter((q): q is string => typeof q === "string") : [],
    },
    tab: academicTabs.includes(candidate.tab as AcademicTab) ? (candidate.tab as AcademicTab) : defaultAcademicTab,
    chapters: Array.isArray(candidate.chapters)
      ? candidate.chapters
          .filter((chapter): chapter is PlannedChapter => Boolean(chapter) && typeof chapter.id === "string" && typeof chapter.title === "string")
          .map((chapter) => ({
            ...chapter,
            status: chapterStatuses.includes(chapter.status) ? chapter.status : "not_started",
            targetWords: typeof chapter.targetWords === "number" ? chapter.targetWords : null,
            notes: typeof chapter.notes === "string" ? chapter.notes : "",
            fromTemplate: chapter.fromTemplate === true,
          }))
      : [],
    evidenceNotes:
      candidate.evidenceNotes && typeof candidate.evidenceNotes === "object"
        ? Object.fromEntries(
            Object.entries(candidate.evidenceNotes).filter(
              ([key, text]) => typeof key === "string" && typeof text === "string" && key.includes("::"),
            ),
          )
        : {},
    sources: Array.isArray(candidate.sources)
      ? candidate.sources
          .filter((source): source is AcademicSource => Boolean(source) && typeof source.id === "string" && typeof source.title === "string")
          .map((source) => ({
            ...source,
            authors: Array.isArray(source.authors) ? source.authors.filter((author): author is string => typeof author === "string") : [],
            access: accessLevels.includes(source.access) ? source.access : "metadata",
            peerReviewed: source.peerReviewed === true,
            retractionStatus:
              source.retractionStatus === "retracted" || source.retractionStatus === "corrected" || source.retractionStatus === "none"
                ? source.retractionStatus
                : "unknown",
          }))
      : [],
  };
  return restored;
}

/**
 * Removes notes whose source no longer exists. Keeping them would leave evidence
 * attributed to a record the student has deleted from their library.
 */
export function pruneEvidenceNotes(notes: Readonly<Record<string, string>>, sources: readonly AcademicSource[]): Record<string, string> {
  const ids = new Set(sources.map((source) => source.id));
  return Object.fromEntries(Object.entries(notes).filter(([key]) => ids.has(key.split("::")[0])));
}

export function readWorkspaceDraft(
  principal: DraftPrincipal | null | undefined,
  store: SessionDraftStore | null = defaultSessionDraftStore(),
): AcademicWorkspaceState | null {
  return readSessionDraft<AcademicWorkspaceState>(store, draftNamespace, principal, parseWorkspaceState);
}

export function writeWorkspaceDraft(
  state: AcademicWorkspaceState,
  principal: DraftPrincipal | null | undefined,
  store: SessionDraftStore | null = defaultSessionDraftStore(),
): boolean {
  return writeSessionDraft(store, draftNamespace, principal, state);
}

export function clearWorkspaceDraft(
  principal: DraftPrincipal | null | undefined,
  store: SessionDraftStore | null = defaultSessionDraftStore(),
): boolean {
  return clearSessionDraft(store, draftNamespace, principal);
}
