"use client";
/**
 * Issue #1259 (Academic Research Studio) — the evidence matrix.
 *
 * Phase B adds automated extraction and provenance. Until then the matrix is a
 * manual review grid the student can actually use: one row per verified source,
 * one column per sub-question in the brief, and a note per cell.
 *
 * The integrity rule is visible rather than implied — a source the student
 * cannot read in full is flagged in its row header, because an excerpt or page
 * number for a work they have not opened cannot be verified. The panel says
 * plainly that notes stay in this browser session and that automated extraction
 * is planned, so nothing about it is a mock of a later phase.
 */
import { noteNeedsPageCaution, evidenceCoverage, evidenceNoteKey } from "./academicStudioState";
import type { AcademicSource } from "./academicSourceLibrary";
import { AcademicNotice } from "./AcademicPrimitives";
import type { AcademicLabelers, AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicEvidencePanelProps = {
  sources: readonly AcademicSource[];
  questions: readonly string[];
  notes: Readonly<Record<string, string>>;
  onNoteChange: (sourceId: string, questionIndex: number, text: string) => void;
  t: AcademicTranslate;
  labels: AcademicLabelers;
};

export function AcademicEvidencePanel({ sources, questions, notes, onNoteChange, t, labels }: AcademicEvidencePanelProps) {
  const usableQuestions = questions.map((question) => question.trim()).filter(Boolean);
  const coverage = evidenceCoverage(sources, notes);

  if (sources.length === 0) {
    return (
      <div className={styles.panelBody}>
        <AcademicNotice tone="info" title={t("evidence.needsSourcesTitle")}>
          {t("evidence.needsSourcesBody")}
        </AcademicNotice>
      </div>
    );
  }

  if (usableQuestions.length === 0) {
    return (
      <div className={styles.panelBody}>
        <AcademicNotice tone="warning" title={t("evidence.needsQuestionsTitle")}>
          {t("evidence.needsQuestionsBody")}
        </AcademicNotice>
      </div>
    );
  }

  return (
    <div className={styles.panelBody}>
      <AcademicNotice tone="info" title={t("evidence.plannedTitle")}>
        {t("evidence.plannedBody")}
      </AcademicNotice>

      <p className={styles.coverageNote} role="status" aria-live="polite">
        {t("evidence.coverage", { filled: String(coverage.filled), sources: String(coverage.sourcesCovered), total: String(sources.length) })}
      </p>

      <div className={styles.tableWrap}>
        <table className={styles.matrix}>
          <caption className={styles.matrixCaption}>{t("evidence.caption")}</caption>
          <thead>
            <tr>
              <th scope="col" className={styles.matrixSourceHead}>
                {t("evidence.sourceColumn")}
              </th>
              {usableQuestions.map((question, index) => (
                <th key={index} scope="col">
                  {question}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sources.map((source, sourceIndex) => {
              const caution = noteNeedsPageCaution(source.access);
              return (
                <tr key={source.id}>
                  <th scope="row" className={styles.matrixRowHead}>
                    <span className={styles.sourceRef}>{`S${sourceIndex + 1}`}</span>
                    <span className={styles.matrixSourceTitle}>{source.title}</span>
                    <span className={styles.matrixSourceMeta}>
                      {labels.sourceType(source.sourceType)} · {labels.access(source.access)}
                    </span>
                    {caution ? <span className={styles.matrixCaution}>{t("evidence.pageCaution")}</span> : null}
                  </th>
                  {usableQuestions.map((question, questionIndex) => {
                    const key = evidenceNoteKey(source.id, questionIndex);
                    const inputId = `${source.id}-q${questionIndex}`;
                    return (
                      <td key={key}>
                        <label className={styles.srLabel} htmlFor={inputId}>
                          {t("evidence.cellLabel", { source: `S${sourceIndex + 1}`, question })}
                        </label>
                        <textarea
                          id={inputId}
                          className={`${styles.control} ${styles.cellTextarea}`}
                          rows={2}
                          maxLength={1200}
                          value={notes[key] ?? ""}
                          placeholder={t("evidence.cellPlaceholder")}
                          aria-describedby={caution ? `${inputId}-caution` : undefined}
                          onChange={(event) => onNoteChange(source.id, questionIndex, event.target.value)}
                        />
                        {caution ? (
                          <span id={`${inputId}-caution`} className={styles.srOnly}>
                            {t("evidence.pageCaution")}
                          </span>
                        ) : null}
                      </td>
                    );
                  })}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
