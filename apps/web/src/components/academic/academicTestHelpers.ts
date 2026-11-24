/**
 * Issue #1259 (Academic Research Studio) — shared test helpers.
 *
 * Not a test file: it holds the synthetic fixtures and in-memory stores the
 * academic tests reuse, so no test needs a browser, a network mock or a real
 * workspace. Everything here is invented data.
 */
import { createEmptySourceDraft, type AcademicSource } from "./academicSourceLibrary";
import type { AcademicLabelers } from "./academicStrings";

/** A complete, synthetic verified record with every bibliographic element set. */
export function verifiedSource(overrides: Partial<AcademicSource> = {}): AcademicSource {
  return {
    ...createEmptySourceDraft(),
    id: "src-fixture-1",
    title: "Synthetic regulation of synthetic medicines",
    authors: ["Rebin Khoshnaw"],
    year: 2023,
    sourceType: "journal_article",
    containerTitle: "Journal of Synthetic Policy",
    volume: "8",
    issue: "2",
    pages: "44-59",
    doi: "10.5555/synth.2023.0008",
    access: "full_text",
    peerReviewed: true,
    retractionStatus: "none",
    addedAt: "2026-01-01T00:00:00.000Z",
    ...overrides,
  };
}

/**
 * Label lookups that echo the option key. Components only need strings, and a
 * deterministic echo makes assertions unambiguous.
 */
export function createLabelsFixture(): AcademicLabelers {
  return {
    discipline: (id) => `discipline:${id}`,
    level: (level) => `level:${level}`,
    style: (style) => `style:${style}`,
    methodology: (methodology) => `methodology:${methodology}`,
    sourceType: (type) => `sourceType:${type}`,
    access: (level) => `access:${level}`,
  };
}

/** In-memory stand-in for the browser storage the draft helpers expect. */
export function createMemoryStore(initial: Record<string, string> = {}) {
  const entries = new Map(Object.entries(initial));
  return {
    entries,
    get length() {
      return entries.size;
    },
    key(index: number) {
      return Array.from(entries.keys())[index] ?? null;
    },
    getItem(key: string) {
      return entries.get(key) ?? null;
    },
    setItem(key: string, value: string) {
      entries.set(key, value);
    },
    removeItem(key: string) {
      entries.delete(key);
    },
  };
}

/** The synthetic identity a draft is scoped to. */
export const fixturePrincipal = { userId: "user-synth-1", workspaceId: "ws-synth-1" };
export const otherPrincipal = { userId: "user-synth-2", workspaceId: "ws-synth-1" };
