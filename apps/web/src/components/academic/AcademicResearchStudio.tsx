"use client";
/**
 * Issue #1259 (Academic Research Studio) — Phase A container.
 *
 * Owns the academic workspace state and wires the four review tabs to it:
 *
 *   Plan      brief review, readiness, blocking issues, staged workflow status
 *   Sources   verified source library, filters, citations in the chosen style
 *   Evidence  manual evidence matrix keyed to the brief's sub-questions
 *   Chapters  level-appropriate chapter skeleton with a personal word plan
 *
 * Standard/business research is untouched: it stays at `/create/research` and is
 * offered here as the other entry, so the academic surface extends the product
 * instead of replacing it.
 *
 * Scope discipline (deliberate, and asserted by the tests):
 * - No network calls. The academic job pipeline, scholarly discovery, evidence
 *   extraction and document export are planned phases and appear as statuses.
 * - No provider, model or cost information anywhere in customer-facing copy.
 * - Drafts persist through the privacy-scoped session draft store, keyed by user
 *   and workspace, and are never read across identities.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { BookOpenCheck, FileText, ShieldCheck } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { createAcademicLabels, createAcademicTranslator } from "./academicStrings";
import { academicAssetsRoute, academicStandardRoute } from "./academicRoutes";
import {
  applyDisciplineDefaults,
  countAccessLevels,
  hasBlockingIssue,
  validateAcademicBrief,
  type AcademicBrief,
} from "./academicBrief";
import type { AcademicSource } from "./academicSourceLibrary";
import {
  clearWorkspaceDraft,
  createInitialWorkspaceState,
  readWorkspaceDraft,
  pruneEvidenceNotes,
  resizeChapterPlan,
  setEvidenceNote,
  writeWorkspaceDraft,
  type AcademicTab,
  type AcademicWorkspaceState,
} from "./academicStudioState";
import { AcademicModeSelector } from "./AcademicModeSelector";
import { AcademicTabs } from "./AcademicTabs";
import { AcademicBriefForm } from "./AcademicBriefForm";
import { AcademicPlanPanel } from "./AcademicPlanPanel";
import { AcademicSourcesPanel } from "./AcademicSourcesPanel";
import { AcademicEvidencePanel } from "./AcademicEvidencePanel";
import { AcademicChaptersPanel } from "./AcademicChaptersPanel";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicResearchStudioProps = {
  /** Optional host override for tests and embedding. */
  initialState?: AcademicWorkspaceState;
};

export function AcademicResearchStudio({ initialState }: AcademicResearchStudioProps = {}) {
  const { user, workspace } = useAuth();
  const { locale } = useLocale();
  const ta = useMemo(() => createAcademicTranslator(locale), [locale]);
  const labels = useMemo(() => createAcademicLabels(ta), [ta]);

  const principal = useMemo(() => ({ userId: user?.id ?? null, workspaceId: workspace?.id ?? null }), [user?.id, workspace?.id]);

  const [state, setState] = useState<AcademicWorkspaceState>(() => initialState ?? createInitialWorkspaceState());
  const [draftStatus, setDraftStatus] = useState("");
  const sources = state.sources;

  // Restore the identity-scoped draft after hydration so the first client render
  // matches the server render. Same shape as `ProductAdInputWizard`: the browser
  // read happens outside the committed render, and a principal change or unmount
  // cancels it so one account's draft can never land in another's session.
  useEffect(() => {
    if (initialState) return;
    let cancelled = false;
    queueMicrotask(() => {
      if (cancelled) return;
      const restored = readWorkspaceDraft(principal);
      if (restored) setState(restored);
    });
    return () => {
      cancelled = true;
    };
  }, [initialState, principal]);

  const brief = state.brief;
  const issues = useMemo(() => validateAcademicBrief(brief), [brief]);
  const blocked = hasBlockingIssue(issues);
  const access = useMemo(() => countAccessLevels(sources), [sources]);

  /** Replacing the library also drops evidence notes that referenced removed records. */
  const setSources = useCallback((next: AcademicSource[]) => {
    setState((current) => ({ ...current, sources: next, evidenceNotes: pruneEvidenceNotes(current.evidenceNotes, next) }));
  }, []);

  const updateBrief = useCallback((patch: Partial<AcademicBrief>) => {
    setState((current) => {
      const nextBrief = { ...current.brief, ...patch };
      const chapters = patch.level && patch.level !== current.brief.level ? resizeChapterPlan(current.chapters, patch.level) : current.chapters;
      return { ...current, brief: nextBrief, chapters };
    });
  }, []);

  const updateRequirements = useCallback((patch: Partial<AcademicBrief["requirements"]>) => {
    setState((current) => ({ ...current, brief: { ...current.brief, requirements: { ...current.brief.requirements, ...patch } } }));
  }, []);

  const applyDiscipline = useCallback(() => {
    setState((current) => ({ ...current, brief: applyDisciplineDefaults(current.brief, current.brief.discipline) }));
    setDraftStatus(ta("plan.disciplineApplied"));
  }, [ta]);

  function selectTab(tab: AcademicTab) {
    setState((current) => ({ ...current, tab }));
  }

  const saveDraft = useCallback(() => {
    const saved = writeWorkspaceDraft(state, principal);
    setDraftStatus(saved ? ta("draft.saved") : ta("draft.unavailable"));
  }, [state, principal, ta]);

  const resetDraft = useCallback(() => {
    clearWorkspaceDraft(principal);
    setState(createInitialWorkspaceState());
    setDraftStatus(ta("draft.cleared"));
  }, [principal, ta]);

  const jumpToField = useCallback((field: string) => {
    if (typeof document === "undefined") return;
    const safe = /^[a-zA-Z0-9-]+$/.test(field) ? field : "";
    const container = safe ? document.querySelector(`[data-academic-field="${safe}"]`) : null;
    const control = container?.querySelector("input, select, textarea") as HTMLElement | null;
    if (control) {
      control.focus();
      control.scrollIntoView({ block: "center" });
      return;
    }
    (document.querySelector("textarea, input") as HTMLElement | null)?.focus();
  }, []);

  const tabCounts: Partial<Record<AcademicTab, string>> = {
    sources: String(sources.length),
    evidence: String(Object.keys(state.evidenceNotes).length),
    chapters: String(state.chapters.length),
  };

  const panelFor = (tab: AcademicTab) => {
    if (tab === "sources") {
      return (
        <AcademicSourcesPanel
          sources={sources}
          onLibraryChange={setSources}
          citationStyle={brief.citationStyle}
          allowedSourceTypes={brief.requirements.allowedSourceTypes}
          requireFullText={brief.requirements.requireFullText}
          t={ta}
          labels={labels}
        />
      );
    }
    if (tab === "evidence") {
      return (
        <AcademicEvidencePanel
          sources={sources}
          questions={brief.reviewQuestions}
          notes={state.evidenceNotes}
          onNoteChange={(sourceId, questionIndex, text) =>
            setState((current) => ({ ...current, evidenceNotes: setEvidenceNote(current.evidenceNotes, sourceId, questionIndex, text) }))
          }
          t={ta}
          labels={labels}
        />
      );
    }
    if (tab === "chapters") {
      return (
        <AcademicChaptersPanel
          chapters={state.chapters}
          level={brief.level}
          wordCountLimit={brief.requirements.wordCount}
          onChange={(chapters) => setState((current) => ({ ...current, chapters }))}
          t={ta}
        />
      );
    }
    return (
      <AcademicPlanPanel
        brief={brief}
        issues={issues}
        sourceCount={sources.length}
        fullTextCount={access.full_text}
        peerReviewedCount={sources.filter((source) => source.peerReviewed).length}
        t={ta}
        labels={labels}
        onJumpToField={jumpToField}
      />
    );
  };

  return (
    <div className={`${styles.page} academic-studio-page`}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>
          <BookOpenCheck size={14} aria-hidden="true" /> {ta("page.eyebrow")}
        </p>
        <h1 className={styles.title}>{ta("page.title")}</h1>
        <p className={styles.lead}>{ta("page.subtitle")}</p>
        <p className={styles.privacyLine}>
          <ShieldCheck size={14} aria-hidden="true" /> {ta("page.privacyLine")}
        </p>
      </header>

      <AcademicModeSelector t={ta} academic />

      <div className={styles.stickyBar}>
        <p className={styles.completeness}>
          {blocked ? ta("bar.blocked") : ta("bar.readyForReview")}
          <span className={styles.completenessDetail}>
            {ta("bar.counts", { sources: String(sources.length), readable: String(access.full_text) })}
          </span>
        </p>
        <div className={styles.barActions}>
          <button type="button" className={styles.primaryButton} onClick={saveDraft}>
            {ta("action.saveDraft")}
          </button>
          <button type="button" className={styles.secondaryButton} onClick={resetDraft}>
            {ta("action.reset")}
          </button>
          <Link href={academicStandardRoute} className={styles.textLink}>
            {ta("action.openStandard")}
          </Link>
        </div>
        <p className={styles.srStatus} role="status" aria-live="polite" aria-atomic="true">
          {draftStatus}
        </p>
      </div>

      <section className={styles.setupSection} aria-labelledby="academic-setup-heading">
        <h2 id="academic-setup-heading" className={styles.sectionHeading}>
          {ta("setup.heading")}
        </h2>
        <p className={styles.sectionIntro}>{ta("setup.intro")}</p>
        <AcademicBriefForm
          brief={brief}
          issues={issues}
          onChange={updateBrief}
          onRequirementsChange={updateRequirements}
          onApplyDisciplineDefaults={applyDiscipline}
          t={ta}
          labels={labels}
        />
      </section>

      <section className={styles.workspaceSection} aria-labelledby="academic-workspace-heading">
        <h2 id="academic-workspace-heading" className={styles.sectionHeading}>
          {ta("workspace.heading")}
        </h2>
        <p className={styles.sectionIntro}>{ta("workspace.intro")}</p>
        <AcademicTabs
          tab={state.tab}
          onSelect={selectTab}
          t={ta}
          labels={{
            plan: ta("tab.plan"),
            sources: ta("tab.sources"),
            evidence: ta("tab.evidence"),
            chapters: ta("tab.chapters"),
          }}
          counts={tabCounts}
          renderPanel={panelFor}
        />
      </section>

      <footer className={styles.footer}>
        <p>{ta("footer.note")}</p>
        <Link href={academicAssetsRoute} className={styles.textLink}>
          <FileText size={14} aria-hidden="true" /> {ta("footer.assets")}
        </Link>
      </footer>
    </div>
  );
}

/**
 * Option labels for the domain catalogues. Built once per locale so the panels
 * stay presentational and the copy lives only in `academicStrings`.
 */
