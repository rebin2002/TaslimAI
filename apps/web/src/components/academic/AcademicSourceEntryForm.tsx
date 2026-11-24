"use client";
/**
 * Issue #1259 (Academic Research Studio) — verified source entry form.
 *
 * The student records a work they have already confirmed exists. Validation is
 * bibliographic, not cosmetic:
 * - a DOI must match the registrant prefix shape and an ISBN must satisfy its
 *   check digit, so a mistyped identifier is caught before it becomes a citation;
 * - publication elements the student has not supplied stay empty and are listed
 *   as missing in the preview, never invented;
 * - the access level is a claim about what the student can read, and "metadata
 *   only" is the default precisely because that is the honest starting point.
 */
import { useState } from "react";
import {
  accessLevels,
  academicSourceTypes,
  createEmptySourceDraft,
  formatCitation,
  validateSourceDraft,
  type AccessLevel,
  type AcademicSourceType,
  type AcademicSource,
  type CitationStyleId,
} from "./academicSourceLibrary";
import { AcademicField, fieldDescribedBy, fieldHasError, useFieldId } from "./AcademicPrimitives";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicSourceEntryFormProps = {
  citationStyle: CitationStyleId;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  /** Returns the outcome so the form can clear itself only on success. */
  onAdd: (source: AcademicSource) => "added" | "duplicate";
  status: string;
};

type DraftState = Omit<AcademicSource, "id"> & { authorsText: string };

function emptyDraft(): DraftState {
  return { ...createEmptySourceDraft(), authorsText: "" };
}

function newId(): string {
  // A random local id is enough: the record belongs to this browser until the
  // academic backend assigns the persistent identity.
  return `src-${Math.random().toString(36).slice(2, 10)}-${Date.now().toString(36)}`;
}

export function AcademicSourceEntryForm({ citationStyle, t, labels, onAdd, status }: AcademicSourceEntryFormProps) {
  const [draft, setDraft] = useState<DraftState>(emptyDraft);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const base = useFieldId("source");

  function set<K extends keyof DraftState>(key: K, value: DraftState[K]) {
    setDraft((current) => ({ ...current, [key]: value }));
  }

  const authorList = draft.authorsText.split(/[;\n]/).map((entry) => entry.trim()).filter(Boolean);
  const preview: AcademicSource = { ...draft, id: "preview", authors: authorList };
  const citation = formatCitation(preview, citationStyle);
  const canPreview = draft.title.trim().length > 0 || authorList.length > 0;

  function nullable(value: string): string | null {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  }

  function submit() {
    const source: Omit<AcademicSource, "id"> = {
      ...draft,
      authors: authorList,
      title: draft.title.trim(),
      containerTitle: nullable(draft.containerTitle ?? ""),
      publisher: nullable(draft.publisher ?? ""),
      edition: nullable(draft.edition ?? ""),
      volume: nullable(draft.volume ?? ""),
      issue: nullable(draft.issue ?? ""),
      pages: nullable(draft.pages ?? ""),
      doi: nullable(draft.doi ?? ""),
      isbn: nullable(draft.isbn ?? ""),
      url: nullable(draft.url ?? ""),
      relevanceNote: nullable(draft.relevanceNote ?? ""),
    };
    const validation = validateSourceDraft(source);
    const messages: Record<string, string> = {};
    for (const [field, key] of Object.entries(validation)) messages[field] = t(asKey(key));
    setErrors(messages);
    if (Object.keys(messages).length > 0) return;
    const outcome = onAdd({ ...source, id: newId() });
    if (outcome === "added") setDraft(emptyDraft());
  }

  const errorIssues = (field: string) => (errors[field] ? [{ field, severity: "error" as const, messageKey: errors[field] }] : []);

  return (
    <section className={styles.section} aria-labelledby="academic-source-entry-heading">
      <h3 id="academic-source-entry-heading" className={styles.sectionHeading}>
        {t("sources.entryTitle")}
      </h3>
      <p className={styles.sectionIntro}>{t("sources.entryIntro")}</p>

      <div className={styles.grid}>
        <AcademicField controlId={`${base}-title`} label={t("sources.fieldTitle")} required issues={errorIssues("title")} t={t}>
          <input
            id={`${base}-title`}
            className={styles.control}
            type="text"
            value={draft.title}
            aria-describedby={fieldDescribedBy(`${base}-title`, { issues: errorIssues("title") })}
            aria-invalid={fieldHasError(errorIssues("title")) || undefined}
            onChange={(event) => set("title", event.target.value)}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-authors`} label={t("sources.fieldAuthors")} hint={t("sources.fieldAuthorsHint")} issues={errorIssues("authors")} t={t}>
          <input
            id={`${base}-authors`}
            className={styles.control}
            type="text"
            value={draft.authorsText}
            placeholder={t("sources.fieldAuthorsPlaceholder")}
            aria-describedby={fieldDescribedBy(`${base}-authors`, { hint: true, issues: errorIssues("authors") })}
            aria-invalid={fieldHasError(errorIssues("authors")) || undefined}
            onChange={(event) => set("authorsText", event.target.value)}
          />
        </AcademicField>
      </div>

      <div className={styles.tripleGrid}>
        <AcademicField controlId={`${base}-year`} label={t("sources.fieldYear")} issues={errorIssues("year")} t={t}>
          <input
            id={`${base}-year`}
            className={styles.control}
            type="number"
            inputMode="numeric"
            min={1500}
            max={2100}
            value={draft.year ?? ""}
            aria-describedby={fieldDescribedBy(`${base}-year`, { issues: errorIssues("year") })}
            aria-invalid={fieldHasError(errorIssues("year")) || undefined}
            onChange={(event) => set("year", event.target.value === "" ? null : Number(event.target.value))}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-type`} label={t("sources.fieldType")} t={t}>
          <select
            id={`${base}-type`}
            className={styles.control}
            value={draft.sourceType}
            onChange={(event) => set("sourceType", event.target.value as AcademicSourceType)}
          >
            {academicSourceTypes.map((type) => (
              <option key={type} value={type}>
                {labels.sourceType(type)}
              </option>
            ))}
          </select>
        </AcademicField>

        <AcademicField controlId={`${base}-access`} label={t("sources.fieldAccess")} hint={t("sources.fieldAccessHint")} t={t}>
          <select
            id={`${base}-access`}
            className={styles.control}
            value={draft.access}
            aria-describedby={fieldDescribedBy(`${base}-access`, { hint: true })}
            onChange={(event) => set("access", event.target.value as AccessLevel)}
          >
            {accessLevels.map((level) => (
              <option key={level} value={level}>
                {labels.access(level)}
              </option>
            ))}
          </select>
        </AcademicField>
      </div>

      <div className={styles.grid}>
        <AcademicField controlId={`${base}-doi`} label={t("sources.fieldDoi")} hint={t("sources.fieldDoiHint")} issues={errorIssues("doi")} t={t}>
          <input
            id={`${base}-doi`}
            className={styles.control}
            type="text"
            inputMode="url"
            value={draft.doi ?? ""}
            placeholder="10.1000/example.2024"
            aria-describedby={fieldDescribedBy(`${base}-doi`, { hint: true, issues: errorIssues("doi") })}
            aria-invalid={fieldHasError(errorIssues("doi")) || undefined}
            onChange={(event) => set("doi", event.target.value)}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-isbn`} label={t("sources.fieldIsbn")} hint={t("sources.fieldIsbnHint")} issues={errorIssues("isbn")} t={t}>
          <input
            id={`${base}-isbn`}
            className={styles.control}
            type="text"
            inputMode="numeric"
            value={draft.isbn ?? ""}
            placeholder="9780123456789"
            aria-describedby={fieldDescribedBy(`${base}-isbn`, { hint: true, issues: errorIssues("isbn") })}
            aria-invalid={fieldHasError(errorIssues("isbn")) || undefined}
            onChange={(event) => set("isbn", event.target.value)}
          />
        </AcademicField>
      </div>

      <div className={styles.grid}>
        <AcademicField controlId={`${base}-container`} label={t("sources.fieldContainer")} t={t}>
          <input
            id={`${base}-container`}
            className={styles.control}
            type="text"
            value={draft.containerTitle ?? ""}
            placeholder={t("sources.fieldContainerPlaceholder")}
            onChange={(event) => set("containerTitle", event.target.value)}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-publisher`} label={t("sources.fieldPublisher")} t={t}>
          <input
            id={`${base}-publisher`}
            className={styles.control}
            type="text"
            value={draft.publisher ?? ""}
            onChange={(event) => set("publisher", event.target.value)}
          />
        </AcademicField>
      </div>

      <div className={styles.tripleGrid}>
        <AcademicField controlId={`${base}-volume`} label={t("sources.fieldVolume")} t={t}>
          <input id={`${base}-volume`} className={styles.control} type="text" value={draft.volume ?? ""} onChange={(event) => set("volume", event.target.value)} />
        </AcademicField>
        <AcademicField controlId={`${base}-issue`} label={t("sources.fieldIssue")} t={t}>
          <input id={`${base}-issue`} className={styles.control} type="text" value={draft.issue ?? ""} onChange={(event) => set("issue", event.target.value)} />
        </AcademicField>
        <AcademicField controlId={`${base}-pages`} label={t("sources.fieldPages")} hint={t("sources.fieldPagesHint")} t={t}>
          <input id={`${base}-pages`} className={styles.control} type="text" value={draft.pages ?? ""} aria-describedby={fieldDescribedBy(`${base}-pages`, { hint: true })} onChange={(event) => set("pages", event.target.value)} />
        </AcademicField>
      </div>

      <AcademicField controlId={`${base}-url`} label={t("sources.fieldUrl")} hint={t("sources.fieldUrlHint")} issues={errorIssues("url")} t={t}>
        <input
          id={`${base}-url`}
          className={styles.control}
          type="url"
          inputMode="url"
          value={draft.url ?? ""}
          placeholder="https://"
          aria-describedby={fieldDescribedBy(`${base}-url`, { hint: true, issues: errorIssues("url") })}
          aria-invalid={fieldHasError(errorIssues("url")) || undefined}
          onChange={(event) => set("url", event.target.value)}
        />
      </AcademicField>

      <label className={styles.checkboxRow}>
        <input type="checkbox" checked={draft.peerReviewed} onChange={(event) => set("peerReviewed", event.target.checked)} />
        <span>{t("sources.fieldPeerReviewed")}</span>
      </label>

      <AcademicField controlId={`${base}-note`} label={t("sources.fieldRelevance")} hint={t("sources.fieldRelevanceHint")} t={t}>
        <textarea
          id={`${base}-note`}
          className={`${styles.control} ${styles.textarea}`}
          rows={2}
          maxLength={600}
          value={draft.relevanceNote ?? ""}
          aria-describedby={fieldDescribedBy(`${base}-note`, { hint: true })}
          onChange={(event) => set("relevanceNote", event.target.value)}
        />
      </AcademicField>

      {canPreview ? (
        <div className={styles.preview}>
          <span className={styles.previewLabel}>{t("sources.preview", { style: labels.style(citationStyle) })}</span>
          <p className={styles.previewText}>{citation.text || t("sources.previewEmpty")}</p>
          {citation.missing.length > 0 ? (
            <p className={styles.previewMissing}>{t("sources.previewMissing", { elements: citation.missing.map((element) => t(`citationElement.${element}`)).join(", ") })}</p>
          ) : null}
        </div>
      ) : null}

      <div className={styles.formActions}>
        <button type="button" className={styles.primaryButton} onClick={submit}>
          {t("sources.add")}
        </button>
        <button type="button" className={styles.secondaryButton} onClick={() => { setDraft(emptyDraft()); setErrors({}); }}>
          {t("sources.clear")}
        </button>
      </div>

      <p className={styles.srStatus} role="status" aria-live="polite" aria-atomic="true">
        {status}
      </p>
    </section>
  );
}
