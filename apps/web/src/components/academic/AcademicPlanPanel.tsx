"use client";
/**
 * Issue #1259 (Academic Research Studio) — the Plan tab.
 *
 * Turns the brief into something a student can check before committing to it:
 * a review summary, a readiness list, the validation issues that still block
 * progress, and the staged academic workflow with each stage's real status.
 *
 * The stage list is the honesty surface of the product: a stage that needs the
 * academic backend is rendered as a labelled status with the reason, never as a
 * button that would appear to start work.
 */
import { summarizeBrief, type AcademicBrief, type BriefIssue } from "./academicBrief";
import {
  academicWorkflowViews,
  availableWorkflowStageCount,
} from "./academicCapabilities";
import { AcademicNotice, AcademicStatusBadge } from "./AcademicPrimitives";
import { AcademicWorkflowPanel } from "./AcademicWorkflowPanel";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicPlanPanelProps = {
  brief: AcademicBrief;
  issues: readonly BriefIssue[];
  sourceCount: number;
  fullTextCount: number;
  peerReviewedCount: number;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  onJumpToField: (field: string) => void;
  workspaceId?: string | null;
  chapterHeadings?: readonly string[];
  language?: string;
};

export function AcademicPlanPanel({ brief, issues, sourceCount, fullTextCount, peerReviewedCount, t, labels, onJumpToField, workspaceId = null, chapterHeadings = [], language = "en" }: AcademicPlanPanelProps) {
  const blocking = issues.filter((issue) => issue.severity === "error");
  const advisories = issues.filter((issue) => issue.severity === "warning");
  const rows = summarizeBrief(brief, labels);
  const stages = academicWorkflowViews();
  const readyStages = availableWorkflowStageCount(stages);
  const minimumSources = brief.requirements.minimumSources;
  const minimumPeerReviewed = brief.requirements.minimumPeerReviewed;

  return (
    <div className={styles.panelBody}>
      <section className={styles.section} aria-labelledby="academic-readiness-heading">
        <h3 id="academic-readiness-heading" className={styles.sectionHeading}>
          {t("plan.readiness")}
          <span className={styles.sectionCount}>
            {blocking.length === 0 ? t("plan.readyToReview") : t("plan.blockedCount", { count: String(blocking.length) })}
          </span>
        </h3>
        <ul className={styles.checkList}>
          <li className={brief.researchQuestion.trim() ? styles.checkDone : styles.checkPending}>
            {t("plan.checkQuestion", { count: String(brief.researchQuestion.trim().length) })}
          </li>
          <li className={brief.methodologies.length > 0 ? styles.checkDone : styles.checkPending}>
            {t("plan.checkMethods", { count: String(brief.methodologies.length) })}
          </li>
          <li className={brief.requirements.allowedSourceTypes.length > 0 ? styles.checkDone : styles.checkPending}>
            {t("plan.checkSourceTypes", { count: String(brief.requirements.allowedSourceTypes.length) })}
          </li>
          <li className={sourceCount > 0 ? styles.checkDone : styles.checkPending}>
            {t("plan.checkLibrary", { count: String(sourceCount) })}
          </li>
          <li className={brief.reviewQuestions.some((question) => question.trim()) ? styles.checkDone : styles.checkPending}>
            {t("plan.checkQuestions", { count: String(brief.reviewQuestions.filter((question) => question.trim()).length) })}
          </li>
        </ul>

        {minimumSources !== null ? (
          <p className={styles.thresholdNote}>
            {sourceCount >= minimumSources ? t("plan.metSourceMinimum", { required: String(minimumSources), actual: String(sourceCount) }) : t("plan.belowSourceMinimum", { required: String(minimumSources), actual: String(sourceCount), remaining: String(minimumSources - sourceCount) })}
          </p>
        ) : null}
        {minimumPeerReviewed !== null ? (
          <p className={styles.thresholdNote}>
            {peerReviewedCount >= minimumPeerReviewed ? t("plan.metPeerMinimum", { required: String(minimumPeerReviewed), actual: String(peerReviewedCount) }) : t("plan.belowPeerMinimum", { required: String(minimumPeerReviewed), actual: String(peerReviewedCount) })}
          </p>
        ) : null}
        {brief.requirements.requireFullText ? (
          <p className={styles.thresholdNote}>{t("plan.fullTextProgress", { readable: String(fullTextCount), total: String(sourceCount) })}</p>
        ) : null}
      </section>

      {blocking.length > 0 || advisories.length > 0 ? (
        <section className={styles.section} aria-labelledby="academic-issues-heading">
          <h3 id="academic-issues-heading" className={styles.sectionHeading}>
            {t("plan.issues")}
          </h3>
          <ul className={styles.issueList}>
            {[...blocking, ...advisories].map((issue, index) => (
              <li key={`${issue.field}-${index}`} className={issue.severity === "error" ? styles.issueError : styles.issueWarning}>
                <button type="button" className={styles.issueButton} onClick={() => onJumpToField(issue.field)}>
                  {issue.severity === "error" ? t("severity.error") : t("severity.warning")}
                </button>
                <span>{t(asKey(issue.messageKey), issue.params)}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section className={styles.section} aria-labelledby="academic-summary-heading">
        <h3 id="academic-summary-heading" className={styles.sectionHeading}>
          {t("plan.summary")}
        </h3>
        <dl className={styles.summaryList}>
          {rows.map((row, index) => (
            <div key={`${row.labelKey}-${index}`} className={styles.summaryRow}>
              <dt>{t(asKey(row.labelKey))}</dt>
              <dd>{row.hintKey ? t(asKey(row.hintKey)) : row.value}</dd>
            </div>
          ))}
        </dl>
      </section>

      <section className={styles.section} aria-labelledby="academic-stages-heading">
        <h3 id="academic-stages-heading" className={styles.sectionHeading}>
          {t("plan.stages")}
          <span className={styles.sectionCount}>{t("plan.stagesCount", { ready: String(readyStages), total: String(stages.length) })}</span>
        </h3>
        <p className={styles.sectionIntro}>{t("plan.stagesIntro")}</p>
        <ol className={styles.stageList}>
          {stages.map((stage) => {
            return (
              <li key={stage.id} className={styles.stageItem} data-status={stage.status}>
                <div className={styles.stageHead}>
                  <span className={styles.stageIndex} aria-hidden="true">
                    {stages.indexOf(stage) + 1}
                  </span>
                  <strong>{t(asKey(stage.labelKey))}</strong>
                  <AcademicStatusBadge status={stage.status} t={t} phase={stage.phase} />
                </div>
                <p className={styles.stageDetail}>{t(asKey(stage.descriptionKey))}</p>
              </li>
            );
          })}
        </ol>
        <AcademicNotice tone="warning" title={t("plan.jobTitle")}>
          {t("plan.jobBody")}
        </AcademicNotice>
      </section>

      <AcademicWorkflowPanel
        key={workspaceId ?? "no-workspace"}
        workspaceId={workspaceId}
        brief={brief}
        chapterHeadings={chapterHeadings}
        language={language}
        canStart={blocking.length === 0}
        t={t}
        labels={labels}
      />
    </div>
  );
}
