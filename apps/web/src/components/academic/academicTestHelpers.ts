/**
 * Issue #1259 (Academic Research Studio) — shared test helpers.
 *
 * Not a test file: it holds the synthetic fixtures and in-memory stores the
 * academic tests reuse, so no test needs a browser, a network mock or a real
 * workspace. Everything here is invented data.
 */
import { createEmptySourceDraft, type AcademicSource } from "./academicSourceLibrary";
import type { AcademicLabelers } from "./academicStrings";
import type { AcademicSearchSource, AcademicSourceSearchResponse } from "@/lib/api";
import { scholarlySearchPageSize } from "./academicScholarlySearch";

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

/**
 * A synthetic scholarly search hit, in exactly the shape the workspace search endpoint returns:
 * bibliographic metadata, a catalogue access marker, and no internal identifier of any kind.
 * Invented data only, like everything else in this file.
 */
export function discoveredRecord(overrides: Partial<AcademicSearchSource> = {}): AcademicSearchSource {
  return {
    title: "Synthetic governance of synthetic data",
    authors: ["Rebin Khoshnaw"],
    publicationYear: 2024,
    sourceType: "journal_article",
    containerTitle: "Journal of Synthetic Studies",
    publisher: "Synthetic Press",
    volume: "12",
    issue: "3",
    pageRange: "41-58",
    pageCount: 18,
    language: "en",
    citationCount: 7,
    doi: "10.5555/synth.2024.0001",
    isbn13: null,
    isbn10: null,
    abstract: null,
    publisherDescription: null,
    landingUrl: "https://synth.example/records/1",
    fullTextUrl: null,
    accessLevel: "metadata",
    fullTextIndicated: false,
    provenance: {
      basis: "scholarly_catalog_metadata",
      retrievedAtUtc: "2026-11-24T07:00:00Z",
      accessDescription: "Bibliographic record found; the content itself has not been read.",
    },
    relevance: 0.5,
    ...overrides,
  };
}

/** A synthetic page of search results, bounded the way the server bounds it. */
export function discoveredPage(overrides: Partial<AcademicSourceSearchResponse> = {}): AcademicSourceSearchResponse {
  return {
    workspaceId: fixturePrincipal.workspaceId,
    page: 1,
    pageSize: scholarlySearchPageSize,
    totalMatched: 1,
    hasMore: false,
    partial: false,
    partialNotice: null,
    sources: [discoveredRecord()],
    doiChecks: [],
    ...overrides,
  };
}
