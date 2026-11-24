"use client";
/**
 * Issue #1267 slice 1 — rendering of one page of scholarly search results.
 *
 * Presentational on purpose: it takes a response the caller already has, so the honesty of what
 * a student reads is testable with fixtures instead of a live network. Every sentence comes from
 * the studio dictionary. Two rules the markup exists to keep:
 *
 * - Metadata is never shown as full text. A record whose catalogue indicated a public full-text
 *   location gets `sources.findIndicated` plus the "this service has not read it" sentence — the
 *   library record it produces keeps the catalogue's own access level, never `full_text`.
 * - Nothing identifies where the record came from beyond "scholarly records" and the retrieval
 *   date. No service name, no internal identifier, no cost or quota vocabulary.
 */
import { doiUrl, isSafeHttpUrl } from "./academicSourceLibrary";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import type { AcademicSourceSearchResponse } from "@/lib/api";
import {
  maxScholarlySearchPage,
  scholarlyDoiCheckKey,
  scholarlySearchMaxDepth,
  scholarlySearchPageSize,
  type ScholarlySearchPageInfo,
  type ScholarlySearchRow,
} from "./academicScholarlySearch";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicScholarlySearchResultsProps = {
  result: AcademicSourceSearchResponse;
  rows: readonly ScholarlySearchRow[];
  page: ScholarlySearchPageInfo;
  /** True when this row is already in the library by title/year/identifier identity. */
  isKnown: (row: ScholarlySearchRow) => boolean;
  /** Controls are inert while a newer search is in flight, so a stale page cannot be clicked. */
  busy: boolean;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  onAdd: (row: ScholarlySearchRow) => void;
  onPage: (page: number) => void;
};

function formatRetrievedDate(iso: string | null): string | null {
  if (!iso) return null;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? null : date.toISOString().slice(0, 10);
}

export function AcademicScholarlySearchResults({ result, rows, page, isKnown, busy, t, labels, onAdd, onPage }: AcademicScholarlySearchResultsProps) {
  if (rows.length === 0) {
    return (
      <>
        {result.partial ? (
          <AcademicPartialNotice t={t} />
        ) : null}
        <p className={styles.emptyState}>{t("sources.findEmpty")}</p>
      </>
    );
  }

  return (
    <>
      {result.partial ? <AcademicPartialNotice t={t} /> : null}

      <p className={styles.filterNote}>{t("sources.findMatches", { count: String(result.totalMatched), shown: String(rows.length) })}</p>
      {page.hasMore ? (
        <p className={styles.filterNote}>
          {t("sources.findMore", { limit: String(Math.min(scholarlySearchMaxDepth, scholarlySearchPageSize * maxScholarlySearchPage())) })}
        </p>
      ) : null}

      {result.doiChecks.length > 0 ? (
        <div className={styles.filterNote} role="note">
          <strong>{t("sources.doiChecksHeading")}</strong>
          <ul className={styles.sourceList}>
            {result.doiChecks.map((check) => (
              <li key={`${check.submittedDoi}-${check.isRegistered}`}>
                {t(asKey(scholarlyDoiCheckKey(check.submittedDoi, check.isRegistered, check.failureCode)), { doi: check.submittedDoi })}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <ul className={styles.sourceList}>
        {rows.map((row) => {
          const known = isKnown(row);
          const link = row.url && isSafeHttpUrl(row.url) ? row.url : null;
          const retrieved = formatRetrievedDate(row.retrievedAt);
          return (
            <li key={row.key} className={styles.sourceRow}>
              <div className={styles.sourceRowHead}>
                <span className={styles.sourceType}>{labels.sourceType(row.sourceType)}</span>
                <span className={styles.accessPill}>{labels.access(row.access)}</span>
                {row.fullTextIndicated ? <span className={styles.accessPill}>{t("sources.findIndicated")}</span> : null}
              </div>
              <p className={styles.citationText}>
                {row.title}
                {row.year != null ? ` (${row.year})` : ""}
              </p>
              <p className={styles.sourceMeta}>{row.authors.length > 0 ? row.authors.join("; ") : t("sources.findNoAuthors")}</p>
              {row.containerTitle || row.publisher ? <p className={styles.sourceMeta}>{[row.containerTitle, row.publisher].filter(Boolean).join(" — ")}</p> : null}
              <div className={styles.sourceMeta}>
                {row.doi ? (
                  <a className={styles.identifierLink} href={doiUrl(row.doi)} target="_blank" rel="noopener noreferrer">
                    {t("sources.doiLabel")}: {row.doi}
                  </a>
                ) : null}
                {row.isbn ? (
                  <span className={styles.identifierText}>
                    {t("sources.isbnLabel")}: {row.isbn}
                  </span>
                ) : null}
                {!row.doi && !row.isbn ? <span className={styles.identifierMissing}>{t("sources.noIdentifier")}</span> : null}
              </div>
              <p className={styles.missingNote}>{t(asKey(row.accessNoteKey))}</p>
              <p className={styles.missingNote}>{t("flag.retractionUnchecked")}</p>
              {row.abstract ? (
                <p className={styles.missingNote}>
                  <strong>{t("sources.findAbstractLabel")}</strong> {row.abstract}
                </p>
              ) : null}
              <p className={styles.missingNote}>{retrieved ? t("sources.findRetrieved", { date: retrieved }) : t("sources.findProvenance")}</p>
              <div className={styles.sourceRowActions}>
                <button type="button" className={styles.secondaryButton} disabled={busy || known} onClick={() => onAdd(row)}>
                  {known ? t("sources.findInLibrary") : t("sources.add")}
                </button>
                {link ? (
                  <a className={styles.textLink} href={link} target="_blank" rel="noopener noreferrer">
                    {t("sources.findOpen")}
                  </a>
                ) : null}
              </div>
            </li>
          );
        })}
      </ul>

      <div className={styles.filterToggles}>
        <button type="button" className={styles.control} disabled={!page.canPrev || busy} onClick={() => onPage(page.page - 1)}>
          {t("sources.findPrevious")}
        </button>
        <button type="button" className={styles.control} disabled={!page.canNext || busy} onClick={() => onPage(page.page + 1)}>
          {t("sources.findNext")}
        </button>
      </div>
    </>
  );
}

function AcademicPartialNotice({ t }: { t: AcademicTranslate }) {
  return (
    <p className={styles.filterNote} role="note">
      <strong>{t("sources.findPartialTitle")}</strong> {t("sources.findPartial")}
    </p>
  );
}
