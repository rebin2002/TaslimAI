"use client";
/**
 * Issue #1259 (Academic Research Studio) — the academic source library.
 *
 * Phase A ships a genuinely working review surface for sources the student has
 * verified themselves: add a record, see the citation rendered in the chosen
 * style, see which elements are still missing, record how far the work can be
 * read, and filter the library by source type, peer review and full text.
 *
 * Automated scholarly discovery and catalogue lookup are planned and labelled as
 * such here. This panel never claims to have found a source, never fabricates an
 * identifier, page range or quotation, and renders no button whose behaviour the
 * backend does not provide yet.
 */
import { useState } from "react";
import {
  academicSourceTypes,
  addSourceToLibrary,
  filterSources,
  type AccessLevel,
  type AcademicSourceType,
  type AcademicSource,
  type CitationStyleId,
  type SourceFilters,
} from "./academicSourceLibrary";
import { AcademicNotice } from "./AcademicPrimitives";
import { AcademicSourceEntryForm } from "./AcademicSourceEntryForm";
import { AcademicSourceRow } from "./AcademicSourceRow";
import type { AcademicLabelers, AcademicTranslate } from "./academicStrings";
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
};

export function AcademicSourcesPanel({ sources, onLibraryChange, citationStyle, allowedSourceTypes, requireFullText, t, labels }: AcademicSourcesPanelProps) {
  const [filters, setFilters] = useState<SourceFilters>({ sourceTypes: [], requireFullText, peerReviewedOnly: false, query: "" });
  const [status, setStatus] = useState("");

  const filtered = filterSources(sources, filters);
  const reviewLabels = new Map(sources.map((source, index) => [source.id, `S${index + 1}`]));

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

  return (
    <div className={styles.panelBody}>
      <AcademicNotice tone="info" title={t("sources.discoveryTitle")}>
        {t("sources.discoveryBody")}
      </AcademicNotice>

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
