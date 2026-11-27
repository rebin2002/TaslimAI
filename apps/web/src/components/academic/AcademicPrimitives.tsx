"use client";
/**
 * Issue #1259 (Academic Research Studio) — shared Phase A primitives.
 *
 * Small, dependency-free building blocks the academic panels compose:
 * - `AcademicStatusBadge` renders a capability's honesty state (available /
 *   planned) as text plus a colour, never as an actionable control.
 * - `AcademicNotice` renders an explanatory note as a non-interrupting region.
 * - `AcademicField` owns the label, help text and error message for one control
 *   and derives deterministic description ids from the control's own id, so the
 *   panels cannot ship an unlabelled or unannounced input.
 *
 * Ids always come from a caller-supplied control id, so two instances on one
 * page never share a description reference and the server/client markup match.
 */
import { useId, type ReactNode } from "react";
import type { AcademicCapabilityStatus } from "./academicCapabilities";
import type { BriefIssue } from "./academicBrief";
import { asKey, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export function AcademicStatusBadge({ status, t, phase }: { status: AcademicCapabilityStatus; t: AcademicTranslate; phase?: string }) {
  const label = status === "available" ? t("status.available") : status === "planned" ? t("status.planned") : t("status.unavailable");
  return (
    <span
      className={`${styles.badge} ${status === "available" ? styles.badgeAvailable : styles.badgePlanned}`}
      data-status={status}
    >
      <span aria-hidden="true" className={styles.badgeDot} />
      {label}
      {phase ? <span className="sr-only">{t("status.phase", { phase })}</span> : null}
    </span>
  );
}

export function AcademicNotice({
  tone = "info",
  title,
  children,
  id,
}: Readonly<{
  tone?: "info" | "warning" | "integrity";
  title?: ReactNode;
  children: ReactNode;
  id?: string;
}>) {
  return (
    <div
      id={id}
      className={`${styles.notice} ${tone === "warning" ? styles.noticeWarning : tone === "integrity" ? styles.noticeIntegrity : ""}`}
      // `note` describes the region without interrupting a screen reader the way
      // a live region would when the panel re-renders.
      role="note"
    >
      {title ? <strong className={styles.noticeTitle}>{title}</strong> : null}
      <div className={styles.noticeBody}>{children}</div>
    </div>
  );
}

export type AcademicFieldProps = {
  /** id of the control rendered as children; every reference derives from it. */
  controlId: string;
  /**
   * Validation field name. Rendered as `data-academic-field` so the Plan tab's
   * issue list can move focus to the control it is complaining about.
   */
  fieldName?: string;
  label: string;
  hint?: string;
  required?: boolean;
  issues?: readonly BriefIssue[];
  t: AcademicTranslate;
  children: ReactNode;
  /** `group` renders fieldset/legend, for checkbox and radio collections. */
  as?: "field" | "group";
  className?: string;
};

/** Id of the nth message of a field, shared by the renderer and the control. */
export function fieldHintId(controlId: string): string {
  return `${controlId}-hint`;
}

export function fieldErrorId(controlId: string, index: number): string {
  return `${controlId}-error-${index}`;
}

/**
 * The `aria-describedby` value a control should carry: help text first, then its
 * current messages, in render order.
 */
export function fieldDescribedBy(controlId: string, options: { hint?: boolean; issues?: readonly BriefIssue[] } = {}): string | undefined {
  const ids: string[] = [];
  if (options.hint) ids.push(fieldHintId(controlId));
  (options.issues ?? []).forEach((_, index) => ids.push(fieldErrorId(controlId, index)));
  return ids.length > 0 ? ids.join(" ") : undefined;
}

export function fieldHasError(issues: readonly BriefIssue[]): boolean {
  return issues.some((issue) => issue.severity === "error");
}

export function AcademicField({ controlId, fieldName, label, hint, required, issues = [], t, children, as = "field", className }: AcademicFieldProps) {
  const errorMessages = issues;
  const descriptions = (
    <>
      {hint ? (
        <span id={fieldHintId(controlId)} className={styles.fieldHint}>
          {hint}
        </span>
      ) : null}
      {errorMessages.map((issue, index) => (
        <span
          key={`${issue.messageKey}-${index}`}
          id={fieldErrorId(controlId, index)}
          className={issue.severity === "error" ? styles.errorText : styles.warningText}
          role={issue.severity === "error" ? "alert" : undefined}
        >
          {issue.severity === "error" ? t("severity.error") : t("severity.warning")} {t(asKey(issue.messageKey), issue.params)}
        </span>
      ))}
    </>
  );

  if (as === "group") {
    return (
      <fieldset className={`${styles.field} ${styles.group} ${className ?? ""}`} data-academic-field={fieldName}>
        <legend className={styles.legend}>
          {label}
          {required ? <span className={styles.requiredMark}> *</span> : null}
        </legend>
        {descriptions}
        {children}
      </fieldset>
    );
  }

  return (
    <div className={`${styles.field} ${className ?? ""}`} data-academic-field={fieldName}>
      <label className={styles.labelText} htmlFor={controlId}>
        {label}
        {required ? <span className={styles.requiredMark}> *</span> : null}
      </label>
      {descriptions}
      {children}
    </div>
  );
}

/** Instance-unique id base for a field inside a panel. */
export function useFieldId(seed: string): string {
  return `${useId()}-${seed}`;
}
