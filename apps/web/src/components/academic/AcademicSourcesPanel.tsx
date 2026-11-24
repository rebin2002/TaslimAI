"use client";
/**
 * Issue #1259 (Academic Research Studio) — the academic source library.
 * Issue #1267 slice 1 — live, read-only scholarly search wired to the workspace endpoint.
 *
 * Two working surfaces, kept deliberately separate:
 *
 * 1. Find scholarly sources — one authenticated, read-only, paginated request per page:
 *    `GET /api/workspaces/{id}/academic-sources/search`. A search returns bibliographic records
 *    (title, authors, identifiers, at most a catalogue abstract). It never claims a work was
 *    read, renders no service, model or cost information, stops paging at the server's own
 *    bound, aborts the previous request when the student searches again, presses Stop or leaves
 *    the tab, and renders rate limits, refusals and partial results as themselves instead of
 *    as "no results". The lifecycle lives in `createScholarlySearchController`, so those
 *    guarantees are asserted without a browser.
 * 2. Record what you verified — manual entry of the works a student confirmed for themselves,
 *    with identifier checking, citation formatting, missing-element reporting and filters.
 *    Nothing about that behaviour changed in this slice.
 *
 * What is NOT here, and is not pretended to be: full-text retrieval, automated screening,
 * evidence extraction, retraction checking, the academic job pipeline and document export. A
 * discovered record enters the library with the catalogue's own access level, `peerReviewed`
 * false and `retractionStatus` "unknown", because discovery asserts none of those things.
 */
import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from "react";
import {
  academicSourceTypes,
  addSourceToLibrary,
  filterSources,
  sourceIdentity,
  type AccessLevel,
  type AcademicSource,
  type AcademicSourceType,
  type CitationStyleId,
  type SourceFilters,
} from "./academicSourceLibrary";
import { AcademicNotice } from "./AcademicPrimitives";
import { AcademicSourceEntryForm } from "./AcademicSourceEntryForm";
import { AcademicSourceRow } from "./AcademicSourceRow";
import { AcademicScholarlySearchResults } from "./AcademicScholarlySearchResults";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import {
  createScholarlySearchController,
  scholarlyRowToSource,
  scholarlySearchFailureIsRetryable,
  scholarlySearchFailureKey,
  scholarlySearchPageSize,
  scholarlySearchPageInfo,
  scholarlySourceId,
  toScholarlySearchRows,
  type ScholarlySearchFormValues,
  type ScholarlySearchRow,
  type ScholarlySearcher,
} from "./academicScholarlySearch";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicSourcesPanelProps = {
  sources: readonly AcademicSource[];
  /** Host owns the library so Plan and Evidence read the same records. */
  onLibraryChange: (next: AcademicSource[]) => void;
  citationStyle: CitationStyleId;
  /** Source types the student's programme accepts; advisory, never enforced. */
  allowedSourceTypes: readonly AcademicSourceType[];
  requireFullText: boolean;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  /**
   * Workspace the search is scoped to. Without it the search surface is honestly absent: the
   * endpoint is workspace-scoped, so there is no meaningful request to make and no fake control
   * to render in its place.
   */
  workspaceId?: string | null;
  /**
   * Transport override for tests and embedding. Defaults to the real API client, so the shipping
   * behaviour is never the stubbed one.
   */
  searchScholarly?: ScholarlySearcher;
};

/**
 * The page size the search asks for. Kept next to the panel so a reader can see the client only
 * ever requests a bounded window; the server clamps it again regardless.
 */
const searchPageSize = scholarlySearchPageSize;

function parseYear(value: string): number | null {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

export function AcademicSourcesPanel({
  sources,
  onLibraryChange,
  citationStyle,
  allowedSourceTypes,
  requireFullText,
  t,
  labels,
  workspaceId = null,
  searchScholarly,
}: AcademicSourcesPanelProps) {
  const [filters, setFilters] = useState<SourceFilters>({ sourceTypes: [], requireFullText, peerReviewedOnly: false, query: "" });
  const [status, setStatus] = useState("");

  // --- scholarly search ---------------------------------------------------------------------
  const [term, setTerm] = useState("");
  const [yearFrom, setYearFrom] = useState("");
  const [yearTo, setYearTo] = useState("");
  const [openAccessOnly, setOpenAccessOnly] = useState(false);
  /** Feedback about what happened to a discovered record in the library; dictionary text only. */
  const [libraryStatus, setLibraryStatus] = useState("");

  const controller = useMemo(() => createScholarlySearchController({ search: searchScholarly, pageSize: searchPageSize }), [searchScholarly]);
  const read = useCallback(() => controller.read(), [controller]);
  const subscribe = useCallback((listener: () => void) => controller.subscribe(listener), [controller]);
  const snapshot = useSyncExternalStore(subscribe, read, read);

  // Leaving the tab, or switching tabs, aborts the in-flight search and detaches the listeners.
  useEffect(() => {
    return () => controller.dispose();
  }, [controller]);

  const scopedWorkspaceId = workspaceId && workspaceId.trim() ? workspaceId.trim() : null;
  const formValues = useCallback(
    (): ScholarlySearchFormValues => ({
      workspaceId: scopedWorkspaceId,
      term,
      yearFrom: parseYear(yearFrom),
      yearTo: parseYear(yearTo),
      openAccessOnly,
    }),
    [openAccessOnly, scopedWorkspaceId, term, yearFrom, yearTo],
  );

  const searching = snapshot.state === "searching";
  const rows = snapshot.result ? toScholarlySearchRows(snapshot.result.sources) : [];
  const pageInfo = snapshot.result ? scholarlySearchPageInfo(snapshot.result, rows) : null;
  const reviewLabels = new Map(sources.map((source, index) => [source.id, `S${index + 1}`]));
  const libraryIdentities = useMemo(() => new Set(sources.map((source) => JSON.stringify(sourceIdentity(source)))), [sources]);
  const isKnown = useCallback(
    (row: ScholarlySearchRow) => libraryIdentities.has(JSON.stringify(sourceIdentity({ title: row.title, year: row.year, doi: row.doi, isbn: row.isbn }))),
    [libraryIdentities],
  );

  function submitSearch(event: { preventDefault: () => void }) {
    event.preventDefault();
    setStatus("");
    setLibraryStatus("");
    controller.search(1, formValues());
  }

  function stopSearch() {
    controller.stop();
  }

  function addDiscoveredSource(row: ScholarlySearchRow) {
    const record = scholarlyRowToSource(row, {
      id: scholarlySourceId(),
      addedAt: new Date().toISOString(),
      accessNote: t(asKey(row.accessNoteKey)),
    });
    const outcome = addSourceToLibrary(sources, record);
    if (outcome.outcome.kind === "duplicate") {
      setLibraryStatus(t("sources.findAddedAlready", { label: reviewLabels.get(outcome.outcome.existingId) ?? "" }));
      return;
    }
    onLibraryChange(outcome.library);
    setLibraryStatus(t("sources.findAdded", { title: row.title }));
  }

  const filtered = filterSources(sources, filters);

  function toggleFilterType(type: AcademicSourceType) {
    setFilters((current) => ({
      ...current,
      sourceTypes: current.sourceTypes.includes(type) ? current.sourceTypes.filter((entry) => entry !== type) : [...current.sourceTypes, type],
    }));
  }

  function changeAccess(source: AcademicSource, access: AccessLevel) {
    onLibraryChange(sources.map((entry) => (entry.id === source.id ? { ...entry, access } : entry)));
    setStatus(t("sources.accessUpdated", { label: reviewLabels.get(source.id) ?? "", access: labels.access(access) }));
  }

  const failure = snapshot.failure;
  const showResults = snapshot.state === "done" && snapshot.result !== null;

  return (
    <div className={styles.panelBody}>
      <AcademicNotice tone="info" title={t("sources.discoveryTitle")}>
        {t("sources.discoveryBody")}
      </AcademicNotice>

      {scopedWorkspaceId ? (
        <section className={styles.section} aria-labelledby="academic-find-heading">
          <h3 id="academic-find-heading" className={styles.sectionHeading}>
            {t("sources.findTitle")}
            {showResults && snapshot.result ? (
              <span className={styles.sectionCount}>{pageInfo ? t("sources.findPage", { page: String(pageInfo.page), pages: String(pageInfo.pageCount) }) : null}</span>
            ) : null}
          </h3>
          <p className={styles.filterNote}>{t("sources.findIntro")}</p>

          <form onSubmit={submitSearch} aria-busy={searching} className={styles.filterBar}>
            <label className={styles.searchField}>
              <span className={styles.srLabel}>{t("sources.findLabel")}</span>
              <input
                className={styles.control}
                type="search"
                value={term}
                placeholder={t("sources.findPlaceholder")}
                onChange={(event) => setTerm(event.target.value)}
              />
            </label>
            <label className={styles.searchField}>
              <span className={styles.srLabel}>{t("sources.findYearFrom")}</span>
              <input className={styles.control} type="number" inputMode="numeric" value={yearFrom} placeholder={t("sources.findYearFromLabel")} onChange={(event) => setYearFrom(event.target.value)} />
            </label>
            <label className={styles.searchField}>
              <span className={styles.srLabel}>{t("sources.findYearTo")}</span>
              <input className={styles.control} type="number" inputMode="numeric" value={yearTo} placeholder={t("sources.findYearToLabel")} onChange={(event) => setYearTo(event.target.value)} />
            </label>
            <label className={styles.checkboxRow}>
              <input type="checkbox" checked={openAccessOnly} onChange={(event) => setOpenAccessOnly(event.target.checked)} />
              <span>{t("sources.findOpenAccessOnly")}</span>
            </label>
            <div className={styles.filterToggles}>
              <button type="submit" className={styles.control} disabled={searching || term.trim().length === 0}>
                {t("sources.findSubmit")}
              </button>
              {searching ? (
                <button type="button" className={styles.control} onClick={stopSearch}>
                  {t("sources.findStop")}
                </button>
              ) : null}
              {failure && scholarlySearchFailureIsRetryable(failure) ? (
                <button type="button" className={styles.control} onClick={() => controller.search(snapshot.page, formValues())}>
                  {t("sources.findRetry")}
                </button>
              ) : null}
            </div>
          </form>

          {searching ? (
            <p role="status" className={styles.filterNote}>
              {t("sources.findBusy")}
            </p>
          ) : null}

          {snapshot.state === "cancelled" ? (
            <p role="status" className={styles.filterNote}>
              {t("sources.findCancelled")}
            </p>
          ) : null}

          {failure && snapshot.state === "failed" ? (
            <AcademicNotice tone="warning" title={t("sources.findProblemTitle")}>
              {failure.kind === "rate_limited"
                ? t(asKey(scholarlySearchFailureKey(failure)), { seconds: String(failure.retryAfterSeconds ?? 60) })
                : t(asKey(scholarlySearchFailureKey(failure)))}
            </AcademicNotice>
          ) : null}

          {showResults && snapshot.result && pageInfo ? (
            <AcademicScholarlySearchResults
              result={snapshot.result}
              rows={rows}
              page={pageInfo}
              isKnown={isKnown}
              busy={searching}
              t={t}
              labels={labels}
              onAdd={addDiscoveredSource}
              onPage={(next) => controller.search(next, formValues())}
            />
          ) : null}

          {!searching && !failure && snapshot.state === "idle" ? <p className={styles.emptyState}>{t("sources.findIdle")}</p> : null}

          {libraryStatus ? (
            <p role="status" className={styles.filterNote}>
              {libraryStatus}
            </p>
          ) : null}
        </section>
      ) : (
        <AcademicNotice tone="warning" title={t("sources.findUnavailableTitle")}>
          {t("sources.findUnavailableBody")}
        </AcademicNotice>
      )}

      <AcademicSourceEntryForm
        citationStyle={citationStyle}
        t={t}
        labels={labels}
        status={status}
        onAdd={(draft) => {
          const result = addSourceToLibrary(sources, draft);
          if (result.outcome.kind === "duplicate") {
            setStatus(t("sources.duplicate", { label: reviewLabels.get(result.outcome.existingId) ?? "" }));
            return "duplicate";
          }
          onLibraryChange(result.library);
          setStatus(t("sources.added", { title: draft.title }));
          return "added";
        }}
      />

      <section className={styles.section} aria-labelledby="academic-library-heading">
        <h3 id="academic-library-heading" className={styles.sectionHeading}>
          {t("sources.library")}
          <span className={styles.sectionCount}>
            {t("sources.count", { visible: String(filtered.visible.length), total: String(sources.length) })}
          </span>
        </h3>

        <div className={styles.filterBar}>
          <div className={styles.filterTypes} role="group" aria-label={t("sources.filterByType")}>
            {academicSourceTypes.map((type) => (
              <label key={type} className={`${styles.chip} ${filters.sourceTypes.includes(type) ? styles.chipActive : ""}`}>
                <input type="checkbox" checked={filters.sourceTypes.includes(type)} onChange={() => toggleFilterType(type)} />
                <span>{labels.sourceType(type)}</span>
              </label>
            ))}
          </div>
          <div className={styles.filterToggles}>
            <label className={styles.checkboxRow}>
              <input
                type="checkbox"
                checked={filters.requireFullText}
                onChange={(event) => setFilters((current) => ({ ...current, requireFullText: event.target.checked }))}
              />
              <span>{t("sources.onlyFullText")}</span>
            </label>
            <label className={styles.checkboxRow}>
              <input
                type="checkbox"
                checked={filters.peerReviewedOnly}
                onChange={(event) => setFilters((current) => ({ ...current, peerReviewedOnly: event.target.checked }))}
              />
              <span>{t("sources.onlyPeerReviewed")}</span>
            </label>
            <label className={styles.searchField}>
              <span className={styles.srLabel}>{t("sources.search")}</span>
              <input
                className={styles.control}
                type="search"
                value={filters.query}
                placeholder={t("sources.searchPlaceholder")}
                onChange={(event) => setFilters((current) => ({ ...current, query: event.target.value }))}
              />
            </label>
          </div>
        </div>

        {allowedSourceTypes.length > 0 && allowedSourceTypes.length < academicSourceTypes.length ? (
          <p className={styles.filterNote}>{t("sources.allowedTypesNote", { types: allowedSourceTypes.map(labels.sourceType).join(", ") })}</p>
        ) : null}

        {filtered.hiddenCount > 0 ? <p className={styles.filterNote}>{t("sources.hidden", { count: String(filtered.hiddenCount) })}</p> : null}

        {sources.length === 0 ? (
          <p className={styles.emptyState}>{t("sources.empty")}</p>
        ) : filtered.visible.length === 0 ? (
          <p className={styles.emptyState}>{t("sources.noneMatch")}</p>
        ) : (
          <ul className={styles.sourceList}>
            {filtered.visible.map((source) => (
              <AcademicSourceRow
                key={source.id}
                source={source}
                label={reviewLabels.get(source.id) ?? ""}
                citationStyle={citationStyle}
                t={t}
                labels={labels}
                onAccessChange={(access) => changeAccess(source, access)}
                onRemove={() => {
                  onLibraryChange(sources.filter((entry) => entry.id !== source.id));
                  setStatus(t("sources.removed", { title: source.title }));
                }}
              />
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
