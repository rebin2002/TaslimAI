/**
 * Issue #1267 slice 1 — the Academic Sources panel's route to the live discovery endpoint.
 *
 * This module owns the transport-shaped half of scholarly search so the panel stays about
 * rendering: request building, response narrowing to the fields a student can act on, and
 * honest failure classification. Rules it enforces:
 *
 * - One in-flight request per panel. A new search or page change aborts the previous request
 *   (`AbortController`), so a fast clicker never renders a stale page as if it were current.
 * - No provider, model, routing or cost identifier is ever read from the response or rendered.
 *   The API already omits them; this module does not re-add them from any other field, and the
 *   server's own English notice strings are never surfaced — every sentence comes from the
 *   studio's dictionary through an i18n key this module returns.
 * - A discovered record keeps the catalogue's own access marker. A full-text location being
 *   indicated is rendered as "a location was pointed at", never as "the work was read", and the
 *   library record it produces stores the access level the catalogue actually supported.
 * - Rate limits, unavailable backends and cross-workspace refusals are distinct states with
 *   distinct copy. A 403 is not rendered as "no results" — pretending a student searched
 *   successfully when the server refused them is exactly the dishonesty this studio avoids.
 */
import { ApiError, api, type AcademicSearchSource, type AcademicSourceSearchResponse } from "@/lib/api";
import { createRequestSequencer } from "@/lib/searchRequestLifecycle";
import {
  academicSourceTypes,
  validateDoi,
  validateIsbn,
  type AcademicSource,
  type AcademicSourceType,
} from "./academicSourceLibrary";

/** Page size the search UI uses. The server clamps any value, including a crafted one. */
export const scholarlySearchPageSize = 10;

/** The server-bounded browse limit, mirrored here only so paging stops honestly. */
export const scholarlySearchMaxDepth = 150;

/** Largest page the client may ask for at a given page size before the server bound is reached. */
export function maxScholarlySearchPage(pageSize: number = scholarlySearchPageSize): number {
  return Math.max(1, Math.floor(scholarlySearchMaxDepth / Math.max(1, pageSize)));
}

export type ScholarlySearchParams = {
  term: string;
  page: number;
  pageSize: number;
  yearFrom?: number | null;
  yearTo?: number | null;
  openAccessOnly?: boolean;
  /** Identifier-shaped parts of the term, sent as `doi`/`isbn` so the server can resolve them. */
  dois?: string[];
  isbns?: string[];
};

export type ScholarlySearchRequest = ScholarlySearchParams & {
  workspaceId: string;
  signal: AbortSignal;
};

export type ScholarlySearchFailure = {
  /**
   * Coarse, customer-safe classification. `forbidden` means the workspace fence refused this
   * student; `unavailable` means the deployment has not enabled discovery; `rate_limited`
   * carries the server's own wait; `cancelled` means the caller stopped the request.
   */
  kind: "cancelled" | "unauthenticated" | "forbidden" | "rate_limited" | "unavailable" | "rejected" | "failed";
  retryAfterSeconds: number | null;
};

/** Dictionary key describing a failure in the student's own language. */
export function scholarlySearchFailureKey(failure: ScholarlySearchFailure): string {
  switch (failure.kind) {
    case "cancelled":
      return "sources.findCancelled";
    case "unauthenticated":
      return "sources.findErrUnauthenticated";
    case "forbidden":
      return "sources.findErrForbidden";
    case "rate_limited":
      return "sources.findErrRateLimited";
    case "unavailable":
      return "sources.findErrUnavailable";
    case "rejected":
      return "sources.findErrRejected";
    default:
      return "sources.findErrFailed";
  }
}

/** True when a failure is worth a retry control; cancelled and refused access are not. */
export function scholarlySearchFailureIsRetryable(failure: ScholarlySearchFailure): boolean {
  return failure.kind === "rate_limited" || failure.kind === "unavailable" || failure.kind === "failed" || failure.kind === "rejected";
}

export type ScholarlySearchOutcome = { ok: true; page: AcademicSourceSearchResponse } | { ok: false; failure: ScholarlySearchFailure };

/**
 * The transport shape a host may inject. It is exactly `api.searchAcademicSources`, so the only
 * default implementation is the real client and a test stub cannot drift from it.
 */
export type ScholarlySearcher = (workspaceId: string, params: ScholarlySearchParams, signal: AbortSignal) => Promise<AcademicSourceSearchResponse>;

/**
 * Splits a typed query into free text plus the identifier-shaped pieces of it. A student who
 * pastes `10.5555/synth.2023.0008` or an ISBN gets an identifier resolution request, not a
 * keyword search for the digits, and the free-text remainder still carries any words around it.
 */
export function splitScholarlyIdentifiers(raw: string): { text: string; dois: string[]; isbns: string[] } {
  const dois: string[] = [];
  const isbns: string[] = [];
  const tokens = raw.split(/[\s,;]+/);
  const words: string[] = [];
  for (const token of tokens) {
    if (!token) continue;
    const doi = validateDoi(token);
    if (doi.ok && doi.normalized) {
      dois.push(doi.normalized);
      continue;
    }
    const isbn = validateIsbn(token);
    if (isbn.ok && isbn.normalized) {
      isbns.push(isbn.normalized);
      continue;
    }
    words.push(token);
  }
  return { text: words.join(" ").trim(), dois, isbns };
}

/**
 * Runs one bounded search. Never throws: every failure arrives as a classified outcome, so the
 * panel can render the difference between "nothing matched", "you may retry in 30 seconds",
 * "this workspace was refused" and "the student stopped the request".
 */
export async function searchScholarlySources(
  request: ScholarlySearchRequest,
  search: (workspaceId: string, params: ScholarlySearchParams, signal: AbortSignal) => Promise<AcademicSourceSearchResponse> = api.searchAcademicSources,
): Promise<ScholarlySearchOutcome> {
  const workspaceId = request.workspaceId.trim();
  if (!workspaceId) return { ok: false, failure: { kind: "forbidden", retryAfterSeconds: null } };

  const page = Math.max(1, Math.floor(request.page));
  const pageSize = Math.max(1, Math.floor(request.pageSize));
  if (!Number.isFinite(pageSize) || page > maxScholarlySearchPage(pageSize)) {
    return { ok: false, failure: { kind: "rejected", retryAfterSeconds: null } };
  }

  const identifiers = splitScholarlyIdentifiers(request.term);
  if (!identifiers.text && identifiers.dois.length === 0 && identifiers.isbns.length === 0) {
    return { ok: false, failure: { kind: "rejected", retryAfterSeconds: null } };
  }

  const params: ScholarlySearchParams = {
    term: identifiers.text,
    page,
    pageSize,
    yearFrom: request.yearFrom ?? null,
    yearTo: request.yearTo ?? null,
    openAccessOnly: request.openAccessOnly ?? false,
    dois: identifiers.dois,
    isbns: identifiers.isbns,
  };

  try {
    const result = await search(workspaceId, params, request.signal);
    return { ok: true, page: result };
  } catch (error) {
    return { ok: false, failure: classifyScholarlySearchFailure(error, request.signal) };
  }
}

export function classifyScholarlySearchFailure(error: unknown, signal: AbortSignal): ScholarlySearchFailure {
  if (signal.aborted || (typeof DOMException !== "undefined" && error instanceof DOMException && error.name === "AbortError")) {
    return { kind: "cancelled", retryAfterSeconds: null };
  }
  if (error instanceof ApiError) {
    if (error.status === 401) return { kind: "unauthenticated", retryAfterSeconds: null };
    if (error.status === 403) return { kind: "forbidden", retryAfterSeconds: null };
    if (error.status === 429) return { kind: "rate_limited", retryAfterSeconds: error.retryAfterSeconds ?? null };
    if (error.status === 503 || error.code === "ACADEMIC_SEARCH_UNAVAILABLE") return { kind: "unavailable", retryAfterSeconds: null };
    if (error.status === 400) return { kind: "rejected", retryAfterSeconds: null };
    return { kind: "failed", retryAfterSeconds: null };
  }
  return { kind: "failed", retryAfterSeconds: null };
}

/**
 * The one field-level narrowing the panel needs: what a student can cite from a hit. The access
 * and provenance sentences are returned as dictionary keys, never as baked-in English, so the
 * panel renders them through the studio translator like every other label.
 */
export type ScholarlySearchRow = {
  key: string;
  title: string;
  authors: string[];
  year: number | null;
  sourceType: AcademicSourceType;
  containerTitle: string | null;
  publisher: string | null;
  volume: string | null;
  issue: string | null;
  pageRange: string | null;
  language: string | null;
  doi: string | null;
  isbn: string | null;
  url: string | null;
  /** Catalogue-supplied abstract text, or null. Never a quotation from the work. */
  abstract: string | null;
  /** "metadata" | "abstract": the content this service actually has to show. */
  access: "metadata" | "abstract";
  fullTextIndicated: boolean;
  /** Dictionary key for the honest sentence about how much of the work this service has seen. */
  accessNoteKey: string;
  /** ISO timestamp of when the record was retrieved, or null when the response omitted it. */
  retrievedAt: string | null;
  citationCount: number | null;
};

export function scholarlyAccessNoteKey(access: "metadata" | "abstract", fullTextIndicated: boolean): string {
  if (fullTextIndicated) return "sources.accessNoteIndicated";
  return access === "abstract" ? "sources.accessNoteAbstract" : "sources.accessNoteMetadata";
}

export function toScholarlySearchRows(sources: readonly AcademicSearchSource[]): ScholarlySearchRow[] {
  return sources.map((source, index) => {
    const identifier = source.doi ?? source.isbn13 ?? source.isbn10 ?? null;
    const access = source.accessLevel === "abstract" ? "abstract" : "metadata";
    return {
      key: `${identifier ?? "row"}-${index}`,
      title: source.title,
      authors: source.authors ?? [],
      year: source.publicationYear,
      sourceType: narrowSourceType(source.sourceType),
      containerTitle: source.containerTitle,
      publisher: source.publisher,
      volume: source.volume,
      issue: source.issue,
      pageRange: source.pageRange,
      language: source.language,
      doi: source.doi,
      isbn: source.isbn13 ?? source.isbn10,
      url: source.landingUrl ?? source.fullTextUrl,
      abstract: access === "abstract" ? source.abstract : null,
      access,
      fullTextIndicated: source.fullTextIndicated,
      accessNoteKey: scholarlyAccessNoteKey(access, source.fullTextIndicated),
      retrievedAt: source.provenance?.retrievedAtUtc ?? null,
      citationCount: source.citationCount,
    };
  });
}

/** Unknown or unexpected classifications stay "other"; the studio never guesses precision. */
export function narrowSourceType(value: string): AcademicSourceType {
  return (academicSourceTypes as readonly string[]).includes(value) ? (value as AcademicSourceType) : "other";
}

/**
 * Turns a discovered row into a library record. Deliberately conservative: nothing is filled in
 * that the catalogue did not return, `peerReviewed` stays false because discovery does not assert
 * it, `retractionStatus` stays "unknown" because retraction checking is a later phase, and the
 * access level is the catalogue's own marker — an indicated full-text location does not become
 * `full_text`, because this service has not read the work.
 */
export function scholarlyRowToSource(row: ScholarlySearchRow, options: { id: string; addedAt: string; accessNote: string }): AcademicSource {
  return {
    id: options.id,
    title: row.title,
    authors: [...row.authors],
    year: row.year,
    sourceType: row.sourceType,
    containerTitle: row.containerTitle,
    publisher: row.publisher,
    edition: null,
    volume: row.volume,
    issue: row.issue,
    pages: row.pageRange,
    doi: row.doi,
    isbn: row.isbn,
    url: row.url,
    access: row.access,
    peerReviewed: false,
    retractionStatus: "unknown",
    relevanceNote: options.accessNote,
    addedAt: options.addedAt,
  };
}

/** Local record id for a discovered source, in the same shape the manual form already uses. */
export function scholarlySourceId(): string {
  return `src-${Math.random().toString(36).slice(2, 10)}-${Date.now().toString(36)}`;
}

export type ScholarlySearchPageInfo = {
  page: number;
  pageSize: number;
  shown: number;
  totalMatched: number;
  /** Pages this search can show, capped by the server-side browse bound. */
  pageCount: number;
  hasMore: boolean;
  canPrev: boolean;
  canNext: boolean;
};

/**
 * Paging facts for the control bar. `pageCount` is derived from what the bounded search actually
 * produced and the server bound, so the Next control never promises a page the API will refuse.
 */
export function scholarlySearchPageInfo(result: AcademicSourceSearchResponse, rows: readonly ScholarlySearchRow[]): ScholarlySearchPageInfo {
  const pageSize = Math.max(1, result.pageSize || scholarlySearchPageSize);
  const page = Math.max(1, result.page);
  const pagesFromTotal = Math.max(1, Math.ceil(result.totalMatched / pageSize));
  const pageCount = Math.min(pagesFromTotal, maxScholarlySearchPage(pageSize));
  return {
    page,
    pageSize,
    shown: rows.length,
    totalMatched: result.totalMatched,
    pageCount,
    hasMore: result.hasMore,
    canPrev: page > 1,
    canNext: result.hasMore && page < pageCount && page < maxScholarlySearchPage(pageSize),
  };
}

/** Dictionary key for one DOI registration outcome. */
export function scholarlyDoiCheckKey(submitted: string, isRegistered: boolean, failureCode: string | null): string {
  if (isRegistered) return "sources.doiCheckRegistered";
  switch (failureCode) {
    case "not_registered":
      return "sources.doiCheckNotRegistered";
    case "invalid_syntax":
      return "sources.doiCheckInvalidSyntax";
    case "rate_limited":
      return "sources.doiCheckRateLimited";
    case "timeout":
      return "sources.doiCheckTimeout";
    case "malformed_response":
      return "sources.doiCheckMalformed";
    case "configuration":
      return "sources.doiCheckConfiguration";
    default:
      return "sources.doiCheckUnavailable";
  }
}

/** Query string a search would produce, kept for tests and deep-link debugging. */
export function scholarlySearchQueryParams(request: ScholarlySearchParams): string {
  const params = new URLSearchParams();
  params.set("q", request.term.trim());
  params.set("page", String(request.page));
  params.set("pageSize", String(request.pageSize));
  if (request.yearFrom) params.set("yearFrom", String(request.yearFrom));
  if (request.yearTo) params.set("yearTo", String(request.yearTo));
  if (request.openAccessOnly) params.set("openAccessOnly", "true");
  for (const doi of request.dois ?? []) params.append("doi", doi);
  for (const isbn of request.isbns ?? []) params.append("isbn", isbn);
  return params.toString();
}

/**
 * The lifecycle the Sources panel renders from. It is deliberately framework-free: the whole
 * question of "which response is still current, what does a stop do, what happens when the panel
 * unmounts mid-search" is the interesting part of this slice, and it is testable exactly here,
 * without a DOM. The panel binds it with `useSyncExternalStore` and renders `read()`.
 *
 * Reuses the shared `createRequestSequencer`, the same guard every other read-only view uses
 * against late responses: starting any newer request invalidates the older one's continuation,
 * so a slow first page can never land after a fast second page and be shown as current.
 */
export type ScholarlySearchState = "idle" | "searching" | "done" | "failed" | "cancelled";

export type ScholarlySearchSnapshot = {
  state: ScholarlySearchState;
  /** The page the student last asked for; `searching` and `failed` render against it. */
  page: number;
  result: AcademicSourceSearchResponse | null;
  failure: ScholarlySearchFailure | null;
};

export type ScholarlySearchFormValues = {
  workspaceId: string | null;
  term: string;
  yearFrom?: number | null;
  yearTo?: number | null;
  openAccessOnly?: boolean;
};

export type ScholarlySearchController = {
  /** Stable between emits, so it is safe as a `useSyncExternalStore` snapshot reader. */
  read(): ScholarlySearchSnapshot;
  subscribe(listener: () => void): () => void;
  search(page: number, form: ScholarlySearchFormValues): void;
  stop(): void;
  /** Unmount: aborts the in-flight request and detaches listeners. Still reusable after. */
  dispose(): void;
};

const idleSnapshot: ScholarlySearchSnapshot = { state: "idle", page: 1, result: null, failure: null };

export function createScholarlySearchController(options: { search?: ScholarlySearcher; pageSize?: number } = {}): ScholarlySearchController {
  const search = options.search;
  const pageSize = options.pageSize ?? scholarlySearchPageSize;
  const sequencer = createRequestSequencer();
  const listeners = new Set<() => void>();
  let snapshot = idleSnapshot;
  let controller: AbortController | null = null;

  function emit(next: ScholarlySearchSnapshot) {
    snapshot = next;
    for (const listener of [...listeners]) listener();
  }

  function abortInFlight() {
    if (!controller) return;
    const abandoned = controller;
    controller = null;
    // Invalidate first: the abandoned request's own continuation must become a no-op even if
    // its transport ignores the abort.
    sequencer.begin();
    abandoned.abort();
  }

  return {
    read: () => snapshot,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    search(page, form) {
      const workspaceId = form.workspaceId?.trim() ?? "";
      if (!workspaceId) {
        // No scope means no request to make. Report it as the refusal it is rather than as an
        // empty result set.
        abortInFlight();
        emit({ state: "failed", page, result: null, failure: { kind: "forbidden", retryAfterSeconds: null } });
        return;
      }

      abortInFlight();
      const active = new AbortController();
      controller = active;
      const requestId = sequencer.begin();
      emit({ state: "searching", page, result: snapshot.result, failure: null });

      void searchScholarlySources({ workspaceId, term: form.term, page, pageSize, yearFrom: form.yearFrom ?? null, yearTo: form.yearTo ?? null, openAccessOnly: form.openAccessOnly ?? false, signal: active.signal }, search).then((outcome) => {
        if (!sequencer.isCurrent(requestId) || active.signal.aborted) return;
        controller = null;
        if (outcome.ok) {
          emit({ state: "done", page, result: outcome.page, failure: null });
          return;
        }
        emit({ state: "failed", page, result: null, failure: outcome.failure });
      });
    },
    stop() {
      abortInFlight();
      emit({ state: "cancelled", page: snapshot.page, result: null, failure: { kind: "cancelled", retryAfterSeconds: null } });
    },
    dispose() {
      abortInFlight();
      listeners.clear();
    },
  };
}
