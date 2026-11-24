"use client";
/**
 * Issue #1259 (Academic Research Studio) — university requirements editor.
 *
 * Records the rules of the student's own programme: word limit, minimum source
 * counts, the source types the programme accepts, whether unreadable sources may
 * be cited, and free-text notes copied from the referencing guide.
 *
 * The studio stores these as the student's statements. It never asserts a rule
 * it has not been told, and it never validates a thesis against them — the
 * numbers only drive honest advice ("your library has 4 of the 15 sources you
 * recorded as required").
 */
import { academicSourceTypes, type AcademicSourceType } from "./academicSourceLibrary";
import { briefLimits, type AcademicBrief, type BriefIssue } from "./academicBrief";
import { AcademicField, fieldDescribedBy, fieldErrorId, fieldHasError, fieldHintId, useFieldId } from "./AcademicPrimitives";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicRequirementsEditorProps = {
  requirements: AcademicBrief["requirements"];
  issues: readonly BriefIssue[];
  onChange: (patch: Partial<AcademicBrief["requirements"]>) => void;
  t: AcademicTranslate;
  labels: AcademicLabelers;
};

function optionalNumber(raw: string): number | null {
  if (raw.trim() === "") return null;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? Math.trunc(parsed) : null;
}

export function AcademicRequirementsEditor({ requirements, issues, onChange, t, labels }: AcademicRequirementsEditorProps) {
  const base = useFieldId("req");
  const issuesFor = (field: string) => issues.filter((issue) => issue.field === field);

  function toggleSourceType(type: AcademicSourceType) {
    const current = requirements.allowedSourceTypes;
    onChange({ allowedSourceTypes: current.includes(type) ? current.filter((entry) => entry !== type) : [...current, type] });
  }

  return (
    <fieldset className={`${styles.fieldset} ${styles.requirementsFieldset}`}>
      <legend className={styles.legend}>{t("field.requirements")}</legend>
      <p className={styles.fieldHint}>{t("field.requirementsHint")}</p>

      <div className={styles.grid}>
        <AcademicField controlId={`${base}-words`} fieldName="wordCount" label={t("field.wordCount")} issues={issuesFor("wordCount")} t={t}>
          <input
            id={`${base}-words`}
            className={styles.control}
            type="number"
            inputMode="numeric"
            min={briefLimits.wordCount.min}
            max={briefLimits.wordCount.max}
            step={500}
            value={requirements.wordCount ?? ""}
            aria-describedby={fieldDescribedBy(`${base}-words`, { issues: issuesFor("wordCount") })}
            aria-invalid={fieldHasError(issuesFor("wordCount")) || undefined}
            onChange={(event) => onChange({ wordCount: optionalNumber(event.target.value) })}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-sources`} fieldName="minimumSources" label={t("field.minimumSources")} issues={issuesFor("minimumSources")} t={t}>
          <input
            id={`${base}-sources`}
            className={styles.control}
            type="number"
            inputMode="numeric"
            min={briefLimits.sourceCount.min}
            max={briefLimits.sourceCount.max}
            value={requirements.minimumSources ?? ""}
            aria-describedby={fieldDescribedBy(`${base}-sources`, { issues: issuesFor("minimumSources") })}
            aria-invalid={fieldHasError(issuesFor("minimumSources")) || undefined}
            onChange={(event) => onChange({ minimumSources: optionalNumber(event.target.value) })}
          />
        </AcademicField>

        <AcademicField controlId={`${base}-peer`} fieldName="minimumPeerReviewed" label={t("field.minimumPeerReviewed")} issues={issuesFor("minimumPeerReviewed")} t={t}>
          <input
            id={`${base}-peer`}
            className={styles.control}
            type="number"
            inputMode="numeric"
            min={briefLimits.sourceCount.min}
            max={briefLimits.sourceCount.max}
            value={requirements.minimumPeerReviewed ?? ""}
            aria-describedby={fieldDescribedBy(`${base}-peer`, { issues: issuesFor("minimumPeerReviewed") })}
            aria-invalid={fieldHasError(issuesFor("minimumPeerReviewed")) || undefined}
            onChange={(event) => onChange({ minimumPeerReviewed: optionalNumber(event.target.value) })}
          />
        </AcademicField>

        <label className={styles.checkboxRow}>
          <input
            type="checkbox"
            checked={requirements.requireFullText}
            onChange={(event) => onChange({ requireFullText: event.target.checked })}
          />
          <span>{t("field.requireFullText")}</span>
        </label>
      </div>

      <div className={styles.subGroup}>
        <span className={styles.subLegend} id={fieldHintId(`${base}-allowed`)}>
          {t("field.allowedSourceTypesHint")}
        </span>
        <div className={styles.chipGrid} role="group" aria-labelledby={fieldHintId(`${base}-allowed`)}>
          {academicSourceTypes.map((type) => {
            const checked = requirements.allowedSourceTypes.includes(type);
            return (
              <label key={type} className={`${styles.chip} ${checked ? styles.chipActive : ""}`}>
                <input
                  type="checkbox"
                  checked={checked}
                  aria-describedby={issuesFor("allowedSourceTypes").length ? fieldErrorId(`${base}-allowed`, 0) : undefined}
                  onChange={() => toggleSourceType(type)}
                />
                <span>{labels.sourceType(type)}</span>
              </label>
            );
          })}
        </div>
        {issuesFor("allowedSourceTypes").map((issue, index) => (
          <span key={`${issue.messageKey}-${index}`} className={styles.warningText} id={fieldErrorId(`${base}-allowed`, index)}>
            {t(asKey(issue.messageKey), issue.params)}
          </span>
        ))}
      </div>

      <AcademicField controlId={`${base}-guide`} fieldName="referencingGuideNotes" label={t("field.guideNotes")} hint={t("field.guideNotesHint")} t={t}>
        <textarea
          id={`${base}-guide`}
          className={`${styles.control} ${styles.textarea}`}
          rows={2}
          maxLength={briefLimits.referencingGuideNotes.max}
          value={requirements.referencingGuideNotes}
          placeholder={t("field.guideNotesPlaceholder")}
          aria-describedby={fieldDescribedBy(`${base}-guide`, { hint: true })}
          onChange={(event) => onChange({ referencingGuideNotes: event.target.value })}
        />
      </AcademicField>
    </fieldset>
  );
}
