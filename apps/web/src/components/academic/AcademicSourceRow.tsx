"use client";
/**
 * Issue #1259 (Academic Research Studio) — one reviewed source row.
 *
 * Shows the citation the student will actually submit, plus the provenance facts
 * that decide whether it can be relied on: the verified identifier, how far the
 * student can read the work, whether it is peer reviewed, and whether a
 * retraction or correction is recorded. Retraction state defaults to "not
 * checked" and is displayed as such — the studio must never imply it has
 * confirmed the status of a work it has not queried.
 */
import {
  accessLevels,
  doiUrl,
  formatCitation,
  isCitableSource,
  sourceIntegrityFlags,
  validateDoi,
  validateIsbn,
  type AccessLevel,
  type AcademicSource,
  type CitationStyleId,
} from "./academicSourceLibrary";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicSourceRowProps = {
  source: AcademicSource;
  /** Positional review label (S1, S2 …), not a search rank. */
  label: string;
  citationStyle: CitationStyleId;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  onAccessChange: (access: AccessLevel) => void;
  onRemove: () => void;
};

export function AcademicSourceRow({ source, label, citationStyle, t, labels, onAccessChange, onRemove }: AcademicSourceRowProps) {
  const citation = formatCitation(source, citationStyle);
  const flags = sourceIntegrityFlags(source);
  const doi = validateDoi(source.doi);
  const isbn = validateIsbn(source.isbn);
  const citable = isCitableSource(source);

  return (
    <li className={`${styles.sourceRow} ${citable ? "" : styles.sourceRowUnverified}`}>
      <div className={styles.sourceRowHead}>
        <span className={styles.sourceRef}>{label}</span>
        <span className={styles.sourceType}>{labels.sourceType(source.sourceType)}</span>
        <span className={`${styles.accessPill} ${source.access === "full_text" ? styles.accessFull : ""}`}>
          {labels.access(source.access)}
        </span>
        {source.peerReviewed ? <span className={styles.peerPill}>{t("sources.peerReviewed")}</span> : null}
      </div>

      <p className={styles.citationText}>{citation.text || t("sources.previewEmpty")}</p>

      {citation.missing.length > 0 ? (
        <p className={styles.missingNote}>
          {t("sources.missing", { elements: citation.missing.map((element) => t(`citationElement.${element}`)).join(", ") })}
        </p>
      ) : null}

      <div className={styles.sourceMeta}>
        {doi.ok && doi.normalized ? (
          <a className={styles.identifierLink} href={doiUrl(doi.normalized)} target="_blank" rel="noopener noreferrer">
            {t("sources.doiLabel")}: {doi.normalized}
          </a>
        ) : null}
        {isbn.ok && isbn.normalized ? (
          <span className={styles.identifierText}>
            {t("sources.isbnLabel")}: {isbn.normalized}
          </span>
        ) : null}
        {!citable ? <span className={styles.identifierMissing}>{t("sources.noIdentifier")}</span> : null}
      </div>

      {flags.length > 0 ? (
        <ul className={styles.flagList}>
          {flags.map((flag) => (
            <li key={flag} className={flag.startsWith("flag.retracted") ? styles.flagAlert : undefined}>
              {t(asKey(flag))}
            </li>
          ))}
        </ul>
      ) : null}

      <div className={styles.sourceRowActions}>
        <label className={styles.inlineSelect}>
          <span className={styles.srLabel}>{t("sources.changeAccess", { label })}</span>
          <select className={styles.control} value={source.access} onChange={(event) => onAccessChange(event.target.value as AccessLevel)}>
            {accessLevels.map((level) => (
              <option key={level} value={level}>
                {labels.access(level)}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className={styles.iconButton} onClick={onRemove}>
          {t("sources.remove")}
          <span className={styles.srLabel}>{t("sources.removeNamed", { label, title: source.title })}</span>
        </button>
      </div>
    </li>
  );
}
