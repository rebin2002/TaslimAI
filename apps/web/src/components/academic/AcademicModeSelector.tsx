"use client";
/**
 * Issue #1259 (Academic Research Studio) — entry point for research mode.
 *
 * The product already ships standard (business/web) research. Academic research
 * must not replace it, so this surface presents both entries side by side:
 * - Standard is a real navigation link to the existing Research Studio.
 * - Academic is the current page and is marked as such for assistive tech.
 *
 * Nothing here mutates the standard studio's state and no control pretends to
 * start a job: starting an academic run needs the backend from a later phase.
 */
import Link from "next/link";
import { ArrowUpRight, BookOpenCheck, Globe } from "lucide-react";
import { academicStandardRoute } from "./academicRoutes";
import type { AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicModeSelectorProps = {
  t: AcademicTranslate;
  /** True while the academic surface is the active mode. */
  academic: boolean;
};

export function AcademicModeSelector({ t, academic }: AcademicModeSelectorProps) {
  return (
    <section className={styles.modeSection} aria-labelledby="academic-mode-heading">
      <h2 id="academic-mode-heading" className={styles.modeHeading}>
        {t("mode.heading")}
      </h2>
      <p className={styles.modeIntro}>{t("mode.intro")}</p>
      <div className={styles.modeGrid}>
        <Link href={academicStandardRoute} className={styles.modeCard}>
          <span className={styles.modeIcon} aria-hidden="true">
            <Globe size={18} />
          </span>
          <span className={styles.modeText}>
            <strong>{t("mode.standardTitle")}</strong>
            <span>{t("mode.standardBody")}</span>
          </span>
          <span className={styles.modeAction}>
            {t("mode.standardAction")}
            <ArrowUpRight size={14} aria-hidden="true" />
          </span>
        </Link>
        <div className={`${styles.modeCard} ${styles.modeCardActive}`} aria-current={academic ? "page" : undefined}>
          <span className={styles.modeIcon} aria-hidden="true">
            <BookOpenCheck size={18} />
          </span>
          <span className={styles.modeText}>
            <strong>{t("mode.academicTitle")}</strong>
            <span>{t("mode.academicBody")}</span>
          </span>
          {academic ? <span className={styles.modeBadge}>{t("mode.current")}</span> : null}
        </div>
      </div>
    </section>
  );
}
