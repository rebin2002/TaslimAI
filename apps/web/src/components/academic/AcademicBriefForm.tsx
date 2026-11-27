"use client";
/**
 * Issue #1259 (Academic Research Studio) — the academic setup form.
 *
 * Captures what makes a brief academic rather than generic: level, discipline,
 * methodology, citation style, the programme's own requirements, and the
 * source-type / full-text filters that will gate discovery later.
 *
 * Boundaries:
 * - It writes to the brief it is handed and nothing else. No job is started, no
 *   source is fetched, no cost is implied.
 * - Every control is labelled and described from the same issue list the Plan
 *   tab shows, and carries `aria-invalid` when that list blocks it.
 * - Filter controls say they configure a later phase instead of pretending to
 *   run a search.
 */
import { useEffect, useState } from "react";
import {
  briefLimits,
  citationStyles,
  disciplines,
  methodologyOptions,
  findDiscipline,
  parseKeywords,
  type AcademicBrief,
  type AcademicLevel,
  type DisciplineId,
  type Methodology,
} from "./academicBrief";
import type { BriefIssue } from "./academicBrief";
import type { CitationStyleId } from "./academicSourceLibrary";
import { AcademicField, fieldDescribedBy, fieldHasError, fieldHintId, useFieldId } from "./AcademicPrimitives";
import { AcademicRequirementsEditor } from "./AcademicRequirementsEditor";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicBriefFormProps = {
  brief: AcademicBrief;
  issues: readonly BriefIssue[];
  onChange: (patch: Partial<AcademicBrief>) => void;
  onRequirementsChange: (patch: Partial<AcademicBrief["requirements"]>) => void;
  onApplyDisciplineDefaults: () => void;
  t: AcademicTranslate;
  labels: AcademicLabelers;
};

export function AcademicBriefForm({ brief, issues, onChange, onRequirementsChange, onApplyDisciplineDefaults, t, labels }: AcademicBriefFormProps) {
  const questionId = useFieldId("question");
  const titleId = useFieldId("title");
  const disciplineId = useFieldId("discipline");
  const styleId = useFieldId("style");
  const levelId = useFieldId("level");
  const methodsId = useFieldId("methods");
  const scopeId = useFieldId("scope");
  const populationId = useFieldId("population");
  const periodId = useFieldId("period");
  const keywordsId = useFieldId("keywords");
  const [keywordText, setKeywordText] = useState(brief.keywords.join(", "));

  // A restored or cleared brief must win over stale typed text; comparing parsed
  // arrays keeps the student's own separators while they type.
  const parsedText = parseKeywords(keywordText);
  const inSync = parsedText.length === brief.keywords.length && parsedText.every((entry, index) => entry === brief.keywords[index]);
  useEffect(() => {
    if (inSync) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- external brief change (draft restore or reset) resyncs the editable text.
    setKeywordText(brief.keywords.join(", "));
  }, [inSync, brief.keywords]);

  const issuesFor = (field: string) => issues.filter((issue) => issue.field === field);
  const discipline = findDiscipline(brief.discipline);

  function toggleMethodology(methodology: Methodology) {
    const next = brief.methodologies.includes(methodology)
      ? brief.methodologies.filter((entry) => entry !== methodology)
      : [...brief.methodologies, methodology];
    onChange({ methodologies: next });
  }

  return (
    <form className={styles.form} onSubmit={(event) => event.preventDefault()} noValidate>
      <AcademicField controlId={levelId} fieldName="level" label={t("field.level")} as="group" t={t}>
        <div className={styles.radioRow}>
          {(["masters", "doctoral"] as AcademicLevel[]).map((level) => (
            <label key={level} className={`${styles.choiceCard} ${brief.level === level ? styles.choiceCardActive : ""}`}>
              <input type="radio" name={`${levelId}-radio`} value={level} checked={brief.level === level} onChange={() => onChange({ level })} />
              <span className={styles.choiceText}>
                <strong>{labels.level(level)}</strong>
                <span>{level === "masters" ? t("field.levelMastersHint") : t("field.levelDoctoralHint")}</span>
              </span>
            </label>
          ))}
        </div>
      </AcademicField>

      <div className={styles.grid}>
        <AcademicField controlId={disciplineId} fieldName="discipline" label={t("field.discipline")} hint={t("field.disciplineHint")} required issues={issuesFor("discipline")} t={t}>
          <select
            id={disciplineId}
            className={styles.control}
            value={brief.discipline}
            aria-describedby={fieldDescribedBy(disciplineId, { hint: true, issues: issuesFor("discipline") })}
            aria-invalid={fieldHasError(issuesFor("discipline")) || undefined}
            onChange={(event) => onChange({ discipline: event.target.value as DisciplineId })}
          >
            {disciplines.map((entry) => (
              <option key={entry.id} value={entry.id}>
                {labels.discipline(entry.id)}
              </option>
            ))}
          </select>
        </AcademicField>

        <AcademicField controlId={styleId} fieldName="citationStyle" label={t("field.citationStyle")} hint={t("field.citationStyleHint")} required issues={issuesFor("citationStyle")} t={t}>
          <select
            id={styleId}
            className={styles.control}
            value={brief.citationStyle}
            aria-describedby={fieldDescribedBy(styleId, { hint: true, issues: issuesFor("citationStyle") })}
            aria-invalid={fieldHasError(issuesFor("citationStyle")) || undefined}
            onChange={(event) => onChange({ citationStyle: event.target.value as CitationStyleId })}
          >
            {citationStyles.map((entry) => (
              <option key={entry} value={entry}>
                {labels.style(entry)}
              </option>
            ))}
          </select>
        </AcademicField>
      </div>

      <div className={styles.inlineAction}>
        <button type="button" className={styles.secondaryButton} onClick={onApplyDisciplineDefaults}>
          {t("action.applyDiscipline")}
        </button>
        <span className={styles.inlineHint}>{t("action.applyDisciplineHint")}</span>
      </div>

      <AcademicField controlId={questionId} fieldName="researchQuestion" label={t("field.question")} hint={t("field.questionHint")} required issues={issuesFor("researchQuestion")} t={t}>
        <textarea
          id={questionId}
          className={`${styles.control} ${styles.textarea}`}
          rows={3}
          maxLength={briefLimits.researchQuestion.max}
          value={brief.researchQuestion}
          placeholder={t("field.questionPlaceholder")}
          aria-describedby={fieldDescribedBy(questionId, { hint: true, issues: issuesFor("researchQuestion") })}
          aria-invalid={fieldHasError(issuesFor("researchQuestion")) || undefined}
          aria-required="true"
          onChange={(event) => onChange({ researchQuestion: event.target.value })}
        />
      </AcademicField>

      <div className={styles.grid}>
        <AcademicField controlId={titleId} fieldName="workingTitle" label={t("field.workingTitle")} issues={issuesFor("workingTitle")} t={t}>
          <input
            id={titleId}
            className={styles.control}
            type="text"
            maxLength={briefLimits.workingTitle.max}
            value={brief.workingTitle}
            placeholder={t("field.workingTitlePlaceholder")}
            aria-describedby={fieldDescribedBy(titleId, { issues: issuesFor("workingTitle") })}
            aria-invalid={fieldHasError(issuesFor("workingTitle")) || undefined}
            onChange={(event) => onChange({ workingTitle: event.target.value })}
          />
        </AcademicField>

        <AcademicField controlId={scopeId} fieldName="geographicScope" label={t("field.geographicScope")} issues={issuesFor("geographicScope")} t={t}>
          <input
            id={scopeId}
            className={styles.control}
            type="text"
            maxLength={briefLimits.geographicScope.max}
            value={brief.geographicScope}
            placeholder={t("field.geographicScopePlaceholder")}
            aria-describedby={fieldDescribedBy(scopeId, { issues: issuesFor("geographicScope") })}
            onChange={(event) => onChange({ geographicScope: event.target.value })}
          />
        </AcademicField>
      </div>

      <div className={styles.grid}>
        <AcademicField controlId={populationId} fieldName="populationOrContext" label={t("field.population")} t={t}>
          <input
            id={populationId}
            className={styles.control}
            type="text"
            maxLength={briefLimits.populationOrContext.max}
            value={brief.populationOrContext}
            placeholder={t("field.populationPlaceholder")}
            onChange={(event) => onChange({ populationOrContext: event.target.value })}
          />
        </AcademicField>

        <AcademicField controlId={periodId} fieldName="timePeriod" label={t("field.timePeriod")} t={t}>
          <input
            id={periodId}
            className={styles.control}
            type="text"
            maxLength={briefLimits.timePeriod.max}
            value={brief.timePeriod}
            placeholder={t("field.timePeriodPlaceholder")}
            onChange={(event) => onChange({ timePeriod: event.target.value })}
          />
        </AcademicField>
      </div>

      <AcademicField controlId={methodsId} fieldName="methodologies" label={t("field.methodologies")} hint={t("field.methodologiesHint")} required issues={issuesFor("methodologies")} as="group" t={t}>
        <div className={styles.checkGrid} aria-labelledby={fieldHintId(methodsId)}>
          {methodologyOptions.map((methodology) => {
            const suggested = discipline.suggestedMethodologies.includes(methodology);
            return (
              <label key={methodology} className={`${styles.checkCard} ${suggested ? styles.checkCardSuggested : ""}`}>
                <input
                  type="checkbox"
                  checked={brief.methodologies.includes(methodology)}
                  aria-invalid={fieldHasError(issuesFor("methodologies")) || undefined}
                  aria-describedby={fieldDescribedBy(methodsId, { hint: true, issues: issuesFor("methodologies") })}
                  onChange={() => toggleMethodology(methodology)}
                />
                <span>{labels.methodology(methodology)}</span>
                {suggested ? <span className={styles.suggestedTag}>{t("tag.commonInField")}</span> : null}
              </label>
            );
          })}
        </div>
      </AcademicField>

      <ReviewQuestionEditor brief={brief} issues={issues} t={t} onChange={onChange} />

      <AcademicField controlId={keywordsId} fieldName="keywords" label={t("field.keywords")} hint={t("field.keywordsHint")} issues={issuesFor("keywords")} t={t}>
        <input
          id={keywordsId}
          className={styles.control}
          type="text"
          value={keywordText}
          placeholder={t("field.keywordsPlaceholder")}
          aria-describedby={fieldDescribedBy(keywordsId, { hint: true, issues: issuesFor("keywords") })}
          aria-invalid={fieldHasError(issuesFor("keywords")) || undefined}
          onChange={(event) => {
            setKeywordText(event.target.value);
            onChange({ keywords: parseKeywords(event.target.value) });
          }}
        />
      </AcademicField>

      <AcademicRequirementsEditor
        requirements={brief.requirements}
        issues={issues}
        onChange={onRequirementsChange}
        t={t}
        labels={labels}
      />
    </form>
  );
}

/** The sub-questions the evidence matrix is built around. Fully local. */
function ReviewQuestionEditor({ brief, issues, t, onChange }: {
  brief: AcademicBrief;
  issues: readonly BriefIssue[];
  t: AcademicTranslate;
  onChange: (patch: Partial<AcademicBrief>) => void;
}) {
  const base = useFieldId("reviewQuestions");
  const listIssues = issues.filter((issue) => issue.field === "reviewQuestions");
  const atLimit = brief.reviewQuestions.length >= briefLimits.maxReviewQuestions;

  return (
    <AcademicField controlId={base} fieldName="reviewQuestions" label={t("field.reviewQuestions")} hint={t("field.reviewQuestionsHint")} issues={listIssues} as="group" t={t}>
      <div className={styles.questionList}>
        {brief.reviewQuestions.map((question, index) => {
          const id = `${base}-${index}`;
          const rowIssues = issues.filter((issue) => issue.field === `reviewQuestion-${index}`);
          return (
            <div key={id} className={styles.questionRow} data-academic-field={`reviewQuestion-${index}`}>
              <label className={styles.labelText} htmlFor={id}>
                {t("field.reviewQuestionN", { n: String(index + 1) })}
              </label>
              <input
                id={id}
                className={styles.control}
                type="text"
                maxLength={briefLimits.reviewQuestion.max}
                value={question}
                placeholder={t("field.reviewQuestionPlaceholder")}
                aria-invalid={fieldHasError(rowIssues) || undefined}
                aria-describedby={fieldDescribedBy(id, { issues: rowIssues })}
                onChange={(event) => {
                  const next = [...brief.reviewQuestions];
                  next[index] = event.target.value;
                  onChange({ reviewQuestions: next });
                }}
              />
              <button
                type="button"
                className={styles.iconButton}
                onClick={() => onChange({ reviewQuestions: brief.reviewQuestions.filter((_, position) => position !== index) })}
                aria-label={t("action.removeQuestion", { n: String(index + 1) })}
              >
                {t("action.removeShort")}
              </button>
              {rowIssues.map((issue, position) => (
                <span key={position} className={styles.errorText} role="alert">
                  {t(asKey(issue.messageKey), issue.params)}
                </span>
              ))}
            </div>
          );
        })}
        <button
          type="button"
          className={styles.secondaryButton}
          // Kept focusable when the limit is reached: a natively disabled button
          // would hide the reason from a keyboard and screen-reader user.
          aria-disabled={atLimit || undefined}
          aria-describedby={fieldHintId(base)}
          onClick={() => {
            if (atLimit) return;
            onChange({ reviewQuestions: [...brief.reviewQuestions, ""] });
          }}
        >
          {t("action.addQuestion")}
        </button>
        {atLimit ? <span className={styles.inlineHint}>{t("field.reviewQuestionsAtLimit", { max: String(briefLimits.maxReviewQuestions) })}</span> : null}
      </div>
    </AcademicField>
  );
}
