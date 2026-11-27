/**
 * Issue #1259 (Academic Research Studio) — Phase A verified source library.
 *
 * Pure, provider-free logic for the student's own source records: identifier
 * validation (DOI / ISBN check digits), duplicate detection, review filters and
 * deterministic citation formatting in the five supported styles.
 *
 * Integrity rules this module enforces, because they cannot be enforced later
 * once a citation has been pasted into a thesis:
 * - A source is only citable when it carries a verifiable identifier or an
 *   explicit URL that the student supplied. Nothing here synthesises one.
 * - Missing bibliographic elements are reported (`missing[]`), never filled in.
 *   In particular page numbers, edition statements and quotations are only ever
 *   echoed from the record the student entered.
 */

export type AcademicSourceType =
  | "journal_article"
  | "book"
  | "book_chapter"
  | "edited_volume"
  | "conference_paper"
  | "thesis"
  | "report"
  | "dataset"
  | "working_paper"
  | "standard_or_guideline"
  | "legislation"
  | "other";

/** How far the student can actually read the work. Metadata is not full text. */
export type AccessLevel = "metadata" | "abstract" | "full_text" | "unavailable";

export const academicSourceTypes: readonly AcademicSourceType[] = [
  "journal_article",
  "book",
  "book_chapter",
  "edited_volume",
  "conference_paper",
  "thesis",
  "report",
  "dataset",
  "working_paper",
  "standard_or_guideline",
  "legislation",
  "other",
];

export const accessLevels: readonly AccessLevel[] = ["metadata", "abstract", "full_text", "unavailable"];

export type RetractionStatus = "unknown" | "none" | "retracted" | "corrected";

export type AcademicSource = {
  id: string;
  title: string;
  authors: string[];
  year: number | null;
  sourceType: AcademicSourceType;
  /** Journal name or host book title. */
  containerTitle: string | null;
  publisher: string | null;
  edition: string | null;
  volume: string | null;
  issue: string | null;
  /** Only ever what the student read on the document itself. */
  pages: string | null;
  doi: string | null;
  isbn: string | null;
  url: string | null;
  access: AccessLevel;
  peerReviewed: boolean;
  retractionStatus: RetractionStatus;
  relevanceNote: string | null;
  addedAt: string;
};

export type IdentifierValidation = {
  ok: boolean;
  normalized: string | null;
  /** i18n key of the failure reason; `null` when valid. */
  errorKey: string | null;
};

const doiPrefixes = ["https://doi.org/", "http://doi.org/", "https://dx.doi.org/", "dx.doi.org", "doi.org", "doi:"];
const doiPattern = /^10\.\d{4,9}\/[^\s]+$/;

/** Trim decoration (resolver prefixes, whitespace) and validate the DOI shape. */
export function validateDoi(raw: string | null | undefined): IdentifierValidation {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return { ok: false, normalized: null, errorKey: "error.doi.empty" };
  let candidate = trimmed;
  for (const prefix of doiPrefixes) {
    const lower = candidate.toLowerCase();
    if (lower.startsWith(prefix.toLowerCase())) {
      candidate = candidate.slice(prefix.length);
      break;
    }
  }
  candidate = candidate.replace(/^\/+/, "").trim();
  if (!doiPattern.test(candidate)) return { ok: false, normalized: null, errorKey: "error.doi.malformed" };
  // A DOI must not carry a trailing slash or query decoration.
  if (candidate.endsWith("/") || candidate.includes("?")) return { ok: false, normalized: null, errorKey: "error.doi.malformed" };
  return { ok: true, normalized: candidate, errorKey: null };
}

/** Resolver link for a validated DOI. Callers must validate first. */
export function doiUrl(normalizedDoi: string): string {
  return `https://doi.org/${normalizedDoi.split("/").map(encodeURIComponent).join("/")}`;
}

function isbnDigits(raw: string): string | null {
  const cleaned = raw.replace(/[-\s]/g, "").toUpperCase();
  if (!/^[0-9]+X?$/.test(cleaned)) return null;
  if (cleaned.length === 10 || cleaned.length === 13) return cleaned;
  // A 978/979-prefixed ISBN-13 is the only other accepted spelling.
  return null;
}

/** ISBN-10 and ISBN-13 check-digit validation with no silent correction. */
export function validateIsbn(raw: string | null | undefined): IdentifierValidation {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return { ok: false, normalized: null, errorKey: "error.isbn.empty" };
  const digits = isbnDigits(trimmed);
  if (!digits) return { ok: false, normalized: null, errorKey: "error.isbn.length" };
  if (digits.length === 10) {
    if (!/^[0-9]{9}[0-9X]$/.test(digits)) return { ok: false, normalized: null, errorKey: "error.isbn.invalid" };
    let sum = 0;
    for (let index = 0; index < 9; index += 1) sum += Number(digits[index]) * (10 - index);
    const check = digits[9] === "X" ? 10 : Number(digits[9]);
    if ((sum + check) % 11 !== 0) return { ok: false, normalized: null, errorKey: "error.isbn.check" };
    return { ok: true, normalized: isbn10To13(digits), errorKey: null };
  }
  if (!/^[0-9]{13}$/.test(digits)) return { ok: false, normalized: null, errorKey: "error.isbn.invalid" };
  let total = 0;
  for (let index = 0; index < 12; index += 1) total += Number(digits[index]) * (index % 2 === 0 ? 1 : 3);
  const check = (10 - (total % 10)) % 10;
  if (check !== Number(digits[12])) return { ok: false, normalized: null, errorKey: "error.isbn.check" };
  // Stored as bare digits: registration-group hyphenation is publisher data we
  // cannot derive, so inventing separators would fabricate bibliographic detail.
  return { ok: true, normalized: digits, errorKey: null };
}

function isbn10To13(isbn10: string): string {
  const body = `978${isbn10.slice(0, 9)}`;
  let total = 0;
  for (let index = 0; index < 12; index += 1) total += Number(body[index]) * (index % 2 === 0 ? 1 : 3);
  return `${body}${(10 - (total % 10)) % 10}`;
}

/** Whitespace/punctuation-insensitive key used for duplicate detection. */
export function normalizeTitle(title: string): string {
  return title.trim().toLowerCase().replace(/\s+/g, " ").replace(/[.,;:!?]+$/, "");
}

export type SourceIdentity = {
  /** Stable duplicate key: identifier first, then bibliographic fingerprint. */
  key: string;
};

export function sourceIdentity(source: Pick<AcademicSource, "title" | "year" | "doi" | "isbn">): SourceIdentity {
  const doi = validateDoi(source.doi);
  if (doi.ok && doi.normalized) return { key: `doi:${doi.normalized.toLowerCase()}` };
  const isbn = validateIsbn(source.isbn);
  if (isbn.ok && isbn.normalized) return { key: `isbn:${isbn.normalized}` };
  return { key: `title:${normalizeTitle(source.title)}|${source.year ?? "nd"}` };
}

export function isDuplicateSource(
  candidate: Pick<AcademicSource, "title" | "year" | "doi" | "isbn">,
  library: readonly AcademicSource[],
): AcademicSource | null {
  const key = sourceIdentity(candidate).key;
  return library.find((entry) => sourceIdentity(entry).key === key) ?? null;
}

export type AddSourceOutcome =
  | { kind: "added"; source: AcademicSource }
  | { kind: "duplicate"; existingId: string };

/** Adds only when the record is unique; duplicates are reported, never merged away. */
export function addSourceToLibrary(library: readonly AcademicSource[], source: AcademicSource): {
  library: AcademicSource[];
  outcome: AddSourceOutcome;
} {
  const duplicate = isDuplicateSource(source, library);
  if (duplicate) return { library: [...library], outcome: { kind: "duplicate", existingId: duplicate.id } };
  return { library: [...library, source], outcome: { kind: "added", source } };
}

export function removeSourceFromLibrary(library: readonly AcademicSource[], id: string): AcademicSource[] {
  return library.filter((entry) => entry.id !== id);
}

export function setSourceAccess(library: readonly AcademicSource[], id: string, access: AccessLevel): AcademicSource[] {
  return library.map((entry) => (entry.id === id ? { ...entry, access } : entry));
}

/** Short display label (S1, S2 …) ordered by insertion, not by rank. */
export function sourceReferenceLabels(library: readonly AcademicSource[]): Map<string, string> {
  const labels = new Map<string, string>();
  library.forEach((entry, index) => labels.set(entry.id, `S${index + 1}`));
  return labels;
}

export type SourceFilters = {
  sourceTypes: AcademicSourceType[];
  requireFullText: boolean;
  peerReviewedOnly: boolean;
  query: string;
};

export const emptySourceFilters: SourceFilters = {
  sourceTypes: [],
  requireFullText: false,
  peerReviewedOnly: false,
  query: "",
};

export type FilteredSources = {
  visible: AcademicSource[];
  hiddenCount: number;
  /** Sources hidden because the student cannot read them in full. */
  metadataOnlyCount: number;
};

/**
 * Applies the source-type and full-text filters. Filters never delete: hidden
 * records stay in the library and the excluded count is reported so the review
 * surface can be honest about what the student is not looking at.
 */
export function filterSources(library: readonly AcademicSource[], filters: SourceFilters): FilteredSources {
  const query = filters.query.trim().toLowerCase();
  const metadataOnlyCount = library.filter((entry) => entry.access !== "full_text").length;
  const visible = library.filter((entry) => {
    if (filters.sourceTypes.length > 0 && !filters.sourceTypes.includes(entry.sourceType)) return false;
    if (filters.requireFullText && entry.access !== "full_text") return false;
    if (filters.peerReviewedOnly && !entry.peerReviewed) return false;
    if (query) {
      const haystack = [entry.title, entry.containerTitle ?? "", entry.publisher ?? "", entry.doi ?? "", entry.isbn ?? "", ...entry.authors]
        .join(" ")
        .toLowerCase();
      if (!haystack.includes(query)) return false;
    }
    return true;
  });
  return { visible, hiddenCount: library.length - visible.length, metadataOnlyCount };
}

/** True when the record is citable: a validated identifier or an explicit URL. */
export function isCitableSource(source: AcademicSource): boolean {
  if (source.doi && validateDoi(source.doi).ok) return true;
  if (source.isbn && validateIsbn(source.isbn).ok) return true;
  return Boolean(source.url && isSafeHttpUrl(source.url));
}

/** Same conservative URL rule the existing Research Studio applies to links. */
export function isSafeHttpUrl(url: string): boolean {
  const trimmed = url.trim();
  if (!trimmed || /[\u0000-\u001f\u007f]/.test(trimmed)) return false;
  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    return false;
  }
  return parsed.protocol === "https:" || parsed.protocol === "http:";
}

export type CitationElement = "authors" | "year" | "title" | "container" | "publisher" | "volume" | "issue" | "pages" | "edition" | "identifier";

export type FormattedCitation = {
  style: CitationStyleId;
  text: string;
  /** Elements the record lacks. The formatter marks them, it never guesses. */
  missing: CitationElement[];
  /** True when every element the style needs is present. */
  complete: boolean;
};

export type CitationStyleId = "apa7" | "harvard" | "chicago" | "ieee" | "vancouver";

const noYear = "n.d.";

function authorPhrase(authors: readonly string[]): string | null {
  const clean = authors.map((name) => name.trim()).filter(Boolean);
  if (clean.length === 0) return null;
  return clean.join("; ");
}

function ieeeAuthors(authors: readonly string[]): string | null {
  // IEEE writes given names as initials: "Rebin Khoshnaw" -> "R. Khoshnaw".
  const shaped = authors.map((name) => name.trim()).filter(Boolean).map((name) => initials(name));
  if (shaped.length === 0) return null;
  if (shaped.length === 1) return shaped[0];
  if (shaped.length === 2) return `${shaped[0]} and ${shaped[1]}`;
  return `${shaped.slice(0, -1).join(", ")}, and ${shaped[shaped.length - 1]}`;
}

function initials(name: string): string {
  // Deterministic given-name reduction only: "Rebin Khoshnaw" -> "R. Khoshnaw".
  // Compound surnames are kept exactly as the student typed them.
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length <= 1) return name.trim();
  const surname = parts.slice(-1)[0];
  const given = parts.slice(0, -1).map((part) => `${part[0].toUpperCase()}.`);
  return `${given.join(" ")} ${surname}`;
}

function apaAuthors(authors: readonly string[]): string | null {
  const clean = authors.map((name) => name.trim()).filter(Boolean);
  if (clean.length === 0) return null;
  const shaped = clean.map((name) => {
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0];
    const surname = parts.slice(-1)[0];
    const given = parts.slice(0, -1).map((part) => `${part[0].toUpperCase()}.`);
    return `${surname}, ${given.join(" ")}`;
  });
  if (shaped.length === 1) return shaped[0];
  if (shaped.length === 2) return `${shaped[0]}, & ${shaped[1]}`;
  return `${shaped.slice(0, -1).join(", ")}, & ${shaped[shaped.length - 1]}`;
}

function vancouverAuthors(authors: readonly string[]): string | null {
  const clean = authors.map((name) => name.trim()).filter(Boolean);
  if (clean.length === 0) return null;
  const shown = clean.slice(0, 6).map(initialsVancouver);
  return clean.length > 6 ? `${shown.join(", ")}, et al.` : shown.join(", ");
}

function initialsVancouver(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 1) return parts[0];
  const surname = parts.slice(-1)[0];
  const given = parts.slice(0, -1).map((part) => part[0].toUpperCase()).join("");
  return `${surname} ${given}`;
}

function join(parts: readonly (string | null | undefined)[]): string {
  return parts.filter((part): part is string => typeof part === "string" && part.trim().length > 0).join(" ").replace(/\s+/g, " ").trim();
}

function trailingPeriod(text: string): string {
  const trimmed = text.trim();
  if (!trimmed) return "";
  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

function identifierSuffix(source: AcademicSource): string | null {
  const doi = validateDoi(source.doi);
  if (doi.ok && doi.normalized) return doiUrl(doi.normalized);
  const isbn = validateIsbn(source.isbn);
  if (isbn.ok && isbn.normalized) return `ISBN ${isbn.normalized}`;
  if (source.url && isSafeHttpUrl(source.url)) return source.url.trim();
  return null;
}

/**
 * Formats one citation. Element order follows the style; absent elements are
 * skipped and listed in `missing` so the UI can show what still has to be
 * verified instead of silently printing an empty placeholder.
 */
export function formatCitation(source: AcademicSource, style: CitationStyleId): FormattedCitation {
  const missing: CitationElement[] = [];
  const year = source.year ? String(source.year) : null;
  if (!source.authors.length) missing.push("authors");
  if (!year) missing.push("year");
  if (!source.title.trim()) missing.push("title");
  const isBookLike = source.sourceType === "book" || source.sourceType === "edited_volume" || source.sourceType === "thesis" || source.sourceType === "report" || source.sourceType === "legislation" || source.sourceType === "standard_or_guideline";
  const container = source.containerTitle?.trim() || null;
  // Journal-like records need a host publication; monograph-like records need a
  // publisher instead. Neither is ever inferred from the source type alone.
  if (!isBookLike && !container) missing.push("container");
  const publisher = source.publisher?.trim() || null;
  if (isBookLike && !publisher) missing.push("publisher");
  const identifier = identifierSuffix(source);
  if (!identifier) missing.push("identifier");

  let text = "";
  if (style === "apa7") {
    const authors = apaAuthors(source.authors);
    const volume = source.volume?.trim() || null;
    const issue = source.issue?.trim() || null;
    const pages = source.pages?.trim() || null;
    if (!volume && !pages && !isBookLike) missing.push("volume");
    const containerPhrase = container ? `${container}${volume ? `, ${volume}` : ""}${issue ? `(${issue})` : ""}${pages ? `, ${pages}` : ""}` : null;
    text = join([
      authors ? trailingPeriod(authors) : null,
      year ? `(${year}).` : `(${noYear}).`,
      source.title.trim() ? trailingPeriod(source.title) : null,
      containerPhrase ? trailingPeriod(containerPhrase) : null,
      publisher && isBookLike ? trailingPeriod(publisher) : null,
      source.edition?.trim() ? trailingPeriod(source.edition) : null,
      identifier,
    ]);
  } else if (style === "harvard") {
    const authors = apaAuthors(source.authors);
    const volume = source.volume?.trim() || null;
    const issue = source.issue?.trim() || null;
    const pages = source.pages?.trim() || null;
    const containerPhrase = container ? `${container}${volume ? `, ${volume}` : ""}${issue ? `(no. ${issue})` : ""}${pages ? `, pp. ${pages}` : ""}` : null;
    text = join([
      authors ? trailingPeriod(authors) : null,
      year ? trailingPeriod(year) : `${noYear}.`,
      source.title.trim() ? trailingPeriod(source.title) : null,
      containerPhrase ? `${containerPhrase}.` : null,
      publisher && isBookLike ? trailingPeriod(publisher) : null,
      identifier,
    ]);
  } else if (style === "chicago") {
    const authors = authorPhrase(source.authors);
    const volume = source.volume?.trim() || null;
    const issue = source.issue?.trim() || null;
    const pages = source.pages?.trim() || null;
    text = join([
      authors ? trailingPeriod(authors) : null,
      source.title.trim() ? `"${source.title.trim()}.` : null,
      container ? `${container}${volume ? `, no. ${volume}` : ""}${issue ? ` (${issue})` : ""}:` : null,
      year ? `${year},` : `${noYear},`,
      pages ? `pp. ${pages}.` : null,
      publisher && isBookLike ? trailingPeriod(publisher) : null,
      identifier,
    ]);
  } else if (style === "ieee") {
    const authors = ieeeAuthors(source.authors);
    const volume = source.volume?.trim() || null;
    const issue = source.issue?.trim() || null;
    const pages = source.pages?.trim() || null;
    // IEEE closes the quoted title with a comma when the host publication
    // follows, and with a full stop when the record ends there.
    const titleSegment = source.title.trim()
      ? `"${source.title.trim()}${container || volume || issue || pages || year ? "," : "."}"`
      : null;
    text = join([
      authors ? `${authors},` : null,
      titleSegment,
      container ? `${container},` : null,
      volume ? `vol. ${volume},` : null,
      issue ? `no. ${issue},` : null,
      pages ? `pp. ${pages},` : null,
      year ? trailingPeriod(year) : `${noYear}.`,
      identifier,
    ]);
  } else {
    const authors = vancouverAuthors(source.authors);
    const volume = source.volume?.trim() || null;
    const issue = source.issue?.trim() || null;
    const pages = source.pages?.trim() || null;
    text = join([
      authors ? `${authors}.` : null,
      source.title.trim() ? `${source.title.trim()}.` : null,
      container ? `${container}.` : null,
      [year, volume, issue, pages ? `:${pages}` : null].filter(Boolean).length
        ? `${year ?? noYear};${volume ?? ""}(${issue ?? ""}):${pages ?? ""}.`
        : null,
      identifier,
    ]);
  }

  return { style, text: text.trim(), missing, complete: missing.length === 0 };
}

/** Plain-language integrity note for a record, derived only from its fields. */
export function sourceIntegrityFlags(source: AcademicSource): string[] {
  const flags: string[] = [];
  if (!isCitableSource(source)) flags.push("flag.noVerifiedIdentifier");
  if (source.access === "metadata") flags.push("flag.metadataOnly");
  if (source.access === "abstract") flags.push("flag.abstractOnly");
  if (source.access === "unavailable") flags.push("flag.unavailable");
  if (source.retractionStatus === "retracted") flags.push("flag.retracted");
  if (source.retractionStatus === "corrected") flags.push("flag.corrected");
  if (source.retractionStatus === "unknown" && source.peerReviewed) flags.push("flag.retractionUnchecked");
  return flags;
}

/**
 * A blank record for the entry form. Every optional element starts empty: the
 * studio must never pre-fill bibliographic data it has not been given.
 */
export function createEmptySourceDraft(now: () => string = () => new Date().toISOString()): Omit<AcademicSource, "id"> {
  return {
    title: "",
    authors: [],
    year: null,
    sourceType: "journal_article",
    containerTitle: null,
    publisher: null,
    edition: null,
    volume: null,
    issue: null,
    pages: null,
    doi: null,
    isbn: null,
    url: null,
    access: "metadata",
    peerReviewed: false,
    retractionStatus: "unknown",
    relevanceNote: null,
    addedAt: now(),
  };
}

/** Field-level validation for the entry form, kept separate from the UI. */
export type SourceDraftErrors = Partial<Record<"title" | "authors" | "year" | "doi" | "isbn" | "url", string>>;

export function validateSourceDraft(draft: {
  title: string;
  authors: readonly string[];
  year: number | null;
  doi: string | null;
  isbn: string | null;
  url: string | null;
  sourceType: AcademicSourceType;
}): SourceDraftErrors {
  const errors: SourceDraftErrors = {};
  if (draft.title.trim().length < 3) errors.title = "sourceError.title";
  if (draft.authors.length === 0 && draft.sourceType !== "report" && draft.sourceType !== "legislation" && draft.sourceType !== "dataset") {
    errors.authors = "sourceError.authors";
  }
  if (draft.year !== null && (!Number.isInteger(draft.year) || draft.year < 1500 || draft.year > 2100)) {
    errors.year = "sourceError.year";
  }
  if (draft.doi && !validateDoi(draft.doi).ok) errors.doi = "sourceError.doi";
  if (draft.isbn && !validateIsbn(draft.isbn).ok) errors.isbn = "sourceError.isbn";
  if (draft.url && !isSafeHttpUrl(draft.url)) errors.url = "sourceError.url";
  return errors;
}
