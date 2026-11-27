"use client";
/**
 * Issue #1259 (Academic Research Studio) — the chapter workspace.
 *
 * Chapter *drafting* and DOCX/PDF delivery belong to a later phase, so this tab
 * offers only what works today: the structural skeleton for the student's level,
 * their own section titles, a planned word allocation per chapter, a status that
 * tracks their own progress, and private notes per chapter.
 *
 * The allocation comparison is advice against the limit the student entered, and
 * the tab states outright that no thesis text is generated here. No export, run
 * or "generate chapter" control exists in this slice.
 */
import {
  chapterStatuses,
  chapterAllocationStatus,
  chapterTemplate,
  updateChapter,
  type ChapterStatus,
  type PlannedChapter,
} from "./academicStudioState";
import { AcademicNotice, AcademicStatusBadge } from "./AcademicPrimitives";
import { asKey, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicChaptersPanelProps = {
  chapters: readonly PlannedChapter[];
  level: "masters" | "doctoral";
  wordCountLimit: number | null;
  onChange: (next: PlannedChapter[]) => void;
  t: AcademicTranslate;
};

export function AcademicChaptersPanel({ chapters, level, wordCountLimit, onChange, t }: AcademicChaptersPanelProps) {
  const allocation = chapterAllocationStatus(chapters, wordCountLimit);
  const allocated = chapters.reduce((total, chapter) => total + (chapter.targetWords ?? 0), 0);
  const fillPercent = wordCountLimit && wordCountLimit > 0 ? Math.min(100, Math.round((allocated / wordCountLimit) * 100)) : 0;
  const formatNumber = (value: number) => value.toLocaleString("en-US");
  const template = chapterTemplate(level);
  const statuses = (status: ChapterStatus) => chapters.filter((chapter) => chapter.status === status).length;

  function patch(id: string, next: Partial<Omit<PlannedChapter, "id">>) {
    onChange(updateChapter(chapters, id, next));
  }

  return (
    <div className={styles.panelBody}>
      <AcademicNotice tone="warning" title={t("chapters.plannedTitle")}>
        {t("chapters.plannedBody")}
      </AcademicNotice>

      <section className={styles.section} aria-labelledby="academic-chapters-heading">
        <h3 id="academic-chapters-heading" className={styles.sectionHeading}>
          {t("chapters.heading", { level: level === "doctoral" ? t("level.doctoral") : t("level.masters") })}
          <span className={styles.sectionCount}>{t("chapters.count", { count: String(chapters.length) })}</span>
        </h3>
        <p className={styles.sectionIntro}>{t("chapters.intro", { sections: String(template.length) })}</p>

        <ul className={styles.chapterList}>
          {chapters.map((chapter, index) => (
            <li key={chapter.id} className={styles.chapterItem}>
              <div className={styles.chapterHead}>
                <span className={styles.chapterIndex} aria-hidden="true">
                  {index + 1}
                </span>
                <label className={styles.srLabel} htmlFor={`${chapter.id}-title`}>
                  {t("chapters.titleFor", { n: String(index + 1) })}
                </label>
                <input
                  id={`${chapter.id}-title`}
                  className={`${styles.control} ${styles.chapterTitle}`}
                  type="text"
                  maxLength={200}
                  value={chapter.title}
                  onChange={(event) => patch(chapter.id, { title: event.target.value, fromTemplate: template.includes(event.target.value) })}
                />
                <label className={styles.inlineSelect}>
                  <span className={styles.srLabel}>{t("chapters.statusFor", { n: String(index + 1) })}</span>
                  <select
                    className={styles.control}
                    value={chapter.status}
                    onChange={(event) => patch(chapter.id, { status: event.target.value as ChapterStatus })}
                  >
                    {chapterStatuses.map((status) => (
                      <option key={status} value={status}>
                        {t(`chapterStatus.${status}`)}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              <div className={styles.chapterMeta}>
                <label className={styles.inlineNumber}>
                  <span>{t("chapters.targetWords")}</span>
                  <input
                    className={styles.control}
                    type="number"
                    inputMode="numeric"
                    min={0}
                    max={200000}
                    step={250}
                    value={chapter.targetWords ?? ""}
                    aria-label={t("chapters.targetWordsFor", { n: String(index + 1) })}
                    onChange={(event) => patch(chapter.id, { targetWords: event.target.value === "" ? null : Math.trunc(Number(event.target.value)) })}
                  />
                </label>
                <label className={styles.chapterNote}>
                  <span className={styles.srLabel}>{t("chapters.notesFor", { n: String(index + 1) })}</span>
                  <textarea
                    className={`${styles.control} ${styles.textarea}`}
                    rows={2}
                    maxLength={800}
                    value={chapter.notes}
                    placeholder={t("chapters.notesPlaceholder")}
                    onChange={(event) => patch(chapter.id, { notes: event.target.value })}
                  />
                </label>
              </div>
            </li>
          ))}
        </ul>
      </section>

      <section className={styles.section} aria-labelledby="academic-allocation-heading">
        <h3 id="academic-allocation-heading" className={styles.sectionHeading}>
          {t("chapters.allocation")}
        </h3>
        {wordCountLimit === null ? (
          <p className={styles.allocationNote}>{t("chapters.noLimitSet")}</p>
        ) : (
          <div
            className={styles.allocationBar}
            role="img"
            aria-label={t(asKey(`allocation.${allocation.kind}`), {
              difference: formatNumber(Math.abs(allocation.difference)),
              limit: formatNumber(wordCountLimit),
              allocated: formatNumber(allocated),
            })}
          >
            <span className={`${styles.allocationFill} ${allocation.kind === "over" ? styles.allocationOver : ""}`} style={{ width: `${fillPercent}%` }} aria-hidden="true" />
          </div>
        )}
        <p className={styles.allocationText}>
          {allocation.kind === "empty"
            ? t("allocation.empty")
            : allocation.kind === "unset"
              ? t("allocation.unset", { allocated: formatNumber(allocation.difference) })
              : allocation.kind === "over"
                ? t("allocation.over", { difference: formatNumber(allocation.difference), limit: formatNumber(wordCountLimit ?? 0) })
                : t("allocation.under", {
                    difference: formatNumber(Math.abs(allocation.difference)),
                    limit: formatNumber(wordCountLimit ?? 0),
                  })}
        </p>
        <div className={styles.statusSummary}>
          {chapterStatuses.map((status) => (
            <span key={status} className={styles.statusPill}>
              {t(`chapterStatus.${status}`)}: {statuses(status)}
            </span>
          ))}
        </div>
        <AcademicNotice tone="integrity" title={t("chapters.integrityTitle")}>
          {t("chapters.integrityBody")}
          <span className={styles.noticeBadge}>
            <AcademicStatusBadge status="planned" t={t} phase="C" />
          </span>
        </AcademicNotice>
      </section>
    </div>
  );
}
