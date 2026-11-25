/**
 * Issue #1259 (Academic Research Studio) — Phase A source library tests.
 *
 * Synthetic fixtures only: invented identifiers, invented titles, no real
 * publication data. The integrity rules are the point of these tests — a
 * formatter that "helpfully" invents a page number or an author is the failure
 * mode this slice exists to prevent.
 */
import { describe, expect, it } from "vitest";
import {
  addSourceToLibrary,
  createEmptySourceDraft,
  doiUrl,
  filterSources,
  formatCitation,
  isCitableSource,
  isDuplicateSource,
  normalizeTitle,
  sourceIdentity,
  sourceIntegrityFlags,
  validateDoi,
  validateIsbn,
  validateSourceDraft,
  type AcademicSource,
  type CitationStyleId,
} from "./academicSourceLibrary";

/** Builds a record from explicit fields so a test states exactly what it claims. */
function sourceFixture(overrides: Partial<AcademicSource> = {}): AcademicSource {
  return {
    ...createEmptySourceDraft(),
    id: "src-synth-1",
    title: "Synthetic study of synthetic policy",
    authors: ["Rebin Khoshnaw", "Halgurd Sadiq"],
    year: 2024,
    sourceType: "journal_article",
    containerTitle: "Journal of Synthetic Pharmacy",
    volume: "12",
    issue: "3",
    pages: "101-118",
    doi: "10.5555/synth.2024.0012",
    access: "full_text",
    peerReviewed: true,
    retractionStatus: "none",
    ...overrides,
  };
}

const styles: CitationStyleId[] = ["apa7", "harvard", "chicago", "ieee", "vancouver"];

describe("DOI validation", () => {
  it("accepts a bare DOI and strips resolver decoration", () => {
    expect(validateDoi("10.5555/synth.2024.0012")).toEqual({ ok: true, normalized: "10.5555/synth.2024.0012", errorKey: null });
    expect(validateDoi("https://doi.org/10.5555/synth.2024.0012").normalized).toBe("10.5555/synth.2024.0012");
    expect(validateDoi("  doi: 10.1016/j.demo.2023.01.002  ").normalized).toBe("10.1016/j.demo.2023.01.002");
  });

  it("rejects anything that is not a registrant-prefixed identifier", () => {
    for (const bad of ["", "   ", "5555/synth", "10/synth", "https://example.com/article", "10.5555/"]) {
      expect(validateDoi(bad).ok, bad).toBe(false);
    }
  });

  it("builds an https resolver link only from a validated DOI", () => {
    expect(doiUrl("10.5555/synth.2024.0012")).toBe("https://doi.org/10.5555/synth.2024.0012");
  });
});

describe("ISBN validation", () => {
  it("accepts a valid ISBN-13 and normalises it without inventing hyphenation", () => {
    // 978-0-306-40615-7 is the canonical teaching example: check digit 7.
    expect(validateIsbn("9780306406157")).toEqual({ ok: true, normalized: "9780306406157", errorKey: null });
    expect(validateIsbn("978-0-306-40615-7").ok).toBe(true);
  });

  it("accepts a valid ISBN-10 and reports its ISBN-13 form", () => {
    // 0-306-40615-2 validates, and converts to the ISBN-13 above.
    expect(validateIsbn("0306406152")).toEqual({ ok: true, normalized: "9780306406157", errorKey: null });
    // 10th character X is a legal check digit.
    expect(validateIsbn("080442957X").ok).toBe(true);
  });

  it("rejects a wrong check digit rather than silently correcting it", () => {
    expect(validateIsbn("9780306406158").errorKey).toBe("error.isbn.check");
    expect(validateIsbn("0306406153").errorKey).toBe("error.isbn.check");
    expect(validateIsbn("978030640615").errorKey).toBe("error.isbn.length");
    expect(validateIsbn("not-an-isbn").errorKey).toBe("error.isbn.length");
  });
});

describe("duplicate detection", () => {
  it("keys on the DOI when one is present", () => {
    const a = sourceFixture({ id: "src-a" });
    const b = sourceFixture({ id: "src-b", title: "Retyped title with a typo", doi: "https://doi.org/10.5555/synth.2024.0012" });
    expect(sourceIdentity(a).key).toBe(sourceIdentity(b).key);
    expect(isDuplicateSource(b, [a])?.id).toBe("src-a");
  });

  it("falls back to title and year when no identifier exists", () => {
    const a = sourceFixture({ id: "src-a", doi: null, isbn: null });
    const b = sourceFixture({ id: "src-b", doi: null, isbn: null, title: "  SYNTHETIC STUDY OF SYNTHETIC POLICY. " });
    expect(normalizeTitle(a.title)).toBe(normalizeTitle(b.title));
    expect(isDuplicateSource(b, [a])?.id).toBe("src-a");
  });

  it("adds a unique record and refuses to store the same record twice", () => {
    const first = sourceFixture({ id: "src-a" });
    const added = addSourceToLibrary([], first);
    expect(added.outcome.kind).toBe("added");
    const again = addSourceToLibrary(added.library, sourceFixture({ id: "src-b" }));
    expect(again.outcome).toEqual({ kind: "duplicate", existingId: "src-a" });
    // The rejected add leaves the library byte-identical.
    expect(again.library).toEqual([first]);
  });
});

describe("citable check", () => {
  it("requires an identifier or a link the student supplied", () => {
    expect(isCitableSource(sourceFixture())).toBe(true);
    expect(isCitableSource(sourceFixture({ doi: null, url: null }))).toBe(false);
    expect(isCitableSource(sourceFixture({ doi: null, url: "https://example.org/thesis" }))).toBe(true);
    // A javascript: URL is not a citation source.
    expect(isCitableSource(sourceFixture({ doi: null, url: "javascript:alert(1)" }))).toBe(false);
  });
});

describe("filters", () => {
  const book = sourceFixture({ id: "src-book", sourceType: "book", isbn: "9780306406157", doi: null, title: "Synthetic monograph", access: "abstract" });
  const article = sourceFixture({ id: "src-article" });
  const unread = sourceFixture({ id: "src-unread", title: "Synthetic paywalled article", access: "unavailable", peerReviewed: false });
  const library = [book, article, unread];

  it("applies source type, full-text and peer-review filters together", () => {
    const result = filterSources(library, { sourceTypes: ["journal_article"], requireFullText: true, peerReviewedOnly: true, query: "" });
    expect(result.visible.map((entry) => entry.id)).toEqual(["src-article"]);
    expect(result.hiddenCount).toBe(2);
    expect(result.metadataOnlyCount).toBe(2);
  });

  it("reports what it hides instead of deleting it", () => {
    const result = filterSources(library, { sourceTypes: [], requireFullText: false, peerReviewedOnly: false, query: "monograph" });
    expect(result.visible.map((entry) => entry.id)).toEqual(["src-book"]);
    expect(result.hiddenCount).toBe(2);
    expect(library).toHaveLength(3);
  });
});

describe("citation formatting", () => {
  it("renders all five supported styles from the same verified record", () => {
    for (const style of styles) {
      const citation = formatCitation(sourceFixture(), style);
      expect(citation.text).toContain("Synthetic study of synthetic policy");
      expect(citation.text).toContain("2024");
      expect(citation.text).toContain("https://doi.org/10.5555/synth.2024.0012");
      expect(citation.style).toBe(style);
    }
  });

  it("keeps style-specific ordering: IEEE quotes the title, Vancouver compresses authors", () => {
    // IEEE keeps the given names as initials and closes the quoted title with a
    // comma because the host publication follows.
    const ieee = formatCitation(sourceFixture(), "ieee").text;
    expect(ieee).toContain('"Synthetic study of synthetic policy,"');
    expect(ieee).toContain("R. Khoshnaw and H. Sadiq,");
    expect(formatCitation(sourceFixture(), "vancouver").text).toContain("Khoshnaw R, Sadiq H.");
    expect(formatCitation(sourceFixture(), "apa7").text).toContain("Khoshnaw, R., & Sadiq, H.");
  });

  it("truncates long author lists instead of dropping them silently", () => {
    const many = sourceFixture({ authors: ["A One", "B Two", "C Three", "D Four", "E Five", "F Six", "G Seven"] });
    expect(formatCitation(many, "vancouver").text).toContain("et al.");
  });

  it("never invents a page range, edition or author", () => {
    const bare = sourceFixture({ pages: null, edition: null, authors: [], doi: null, url: null, containerTitle: null, publisher: null });
    for (const style of styles) {
      const citation = formatCitation(bare, style);
      expect(citation.text).not.toMatch(/pp\.\s\d/);
      expect(citation.text).not.toMatch(/\b\d+–\d+\b/);
      expect(citation.missing).toContain("authors");
      expect(citation.complete).toBe(false);
    }
  });

  it("lists exactly the elements that are still missing", () => {
    const noPages = sourceFixture({ pages: null });
    const citation = formatCitation(noPages, "apa7");
    // APA does not require pages, so a missing page range is not reported as a gap.
    expect(citation.missing).not.toContain("pages");
    expect(citation.complete).toBe(true);

    const bookRecord = sourceFixture({ sourceType: "book", publisher: null, containerTitle: null, doi: null, isbn: "9780306406157" });
    expect(formatCitation(bookRecord, "apa7").missing).toContain("publisher");
  });
});

describe("entry validation and integrity flags", () => {
  it("rejects an incomplete record before it reaches the library", () => {
    const errors = validateSourceDraft({ title: "", authors: [], year: 2200, doi: "abc", isbn: "9780306406158", url: "javascript:alert(1)", sourceType: "journal_article" });
    expect(Object.keys(errors).sort()).toEqual(["authors", "doi", "isbn", "title", "url", "year"]);
  });

  it("does not demand authors for institutional works", () => {
    const errors = validateSourceDraft({ title: "Synthetic policy brief", authors: [], year: 2023, doi: null, isbn: null, url: null, sourceType: "report" });
    expect(errors.authors).toBeUndefined();
  });

  it("treats metadata-only access as a review flag, not a blocker", () => {
    const flags = sourceIntegrityFlags(sourceFixture({ access: "metadata" }));
    expect(flags).toContain("flag.metadataOnly");
    expect(flags).not.toContain("flag.retracted");
  });

  it("surfaces a retraction so it cannot be cited as support", () => {
    expect(sourceIntegrityFlags(sourceFixture({ retractionStatus: "retracted" }))).toContain("flag.retracted");
    expect(sourceIntegrityFlags(sourceFixture({ retractionStatus: "corrected" }))).toContain("flag.corrected");
    // Peer reviewed but never checked: the studio says so rather than implying it verified.
    expect(sourceIntegrityFlags(sourceFixture({ retractionStatus: "unknown", peerReviewed: true }))).toContain("flag.retractionUnchecked");
  });
});
