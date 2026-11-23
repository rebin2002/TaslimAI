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
 *
 * Principal binding (privacy, operator review of #1261):
 * - The workspace state lives in an identity-keyed subtree, so a user or
 *   workspace change under a live instance destroys the previous student's
 *   brief, sources, evidence notes, chapter plan, nested input buffers and
 *   draft status synchronously, before the next paint.
 * - Every read and write of the draft store is fenced on the owner the state
 *   was created for: a mismatch renders nothing and refuses to persist, so a
 *   stale in-memory draft can never be written under a new principal's key.
 * - The tab revalidates its session when it returns to the foreground, in the
 *   same shape as `app/account/memory/layout.tsx`, because a second same-origin
 *   tab can replace the shared cookie-backed session underneath this one.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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

/** The authenticated scope a draft belongs to: user identity plus workspace. */
type AcademicDraftPrincipal = {
  userId: string | null;
  workspaceId: string | null;
};

/**
 * Stable, non-secret scope label for one (user, workspace) principal. Used only
 * as a React reconciliation key so an identity change remounts the workspace
 * subtree; it is never rendered, persisted or sent anywhere.
 */
function academicOwnerKey(principal: AcademicDraftPrincipal): string {
  return `${principal.userId ?? "anon"}::${principal.workspaceId ?? "none"}`;
}

export function AcademicResearchStudio({ initialState }: AcademicResearchStudioProps = {}) {
  const { user, workspace, refresh } = useAuth();

  const principal = useMemo(
    () => ({ userId: user?.id ?? null, workspaceId: workspace?.id ?? null }),
    [user?.id, workspace?.id],
  );

  // A second tab signing in as another student replaces the shared cookie-backed
  // session without touching this tab's in-memory tree. Revalidate on foreground
  // so the identity-keyed remount below happens on the next render rather than
  // only after a reload; the academic surface itself still makes no other call.
  useEffect(() => {
    let inFlight = false;
    const revalidate = () => {
      if (typeof document === "undefined" || document.visibilityState !== "visible" || inFlight) return;
      inFlight = true;
      void Promise.resolve().then(() => refresh()).finally(() => {
        inFlight = false;
      });
    };
    if (typeof document === "undefined") return;
    document.addEventListener("visibilitychange", revalidate);
    window.addEventListener("focus", revalidate);
    return () => {
      document.removeEventListener("visibilitychange", revalidate);
      window.removeEventListener("focus", revalidate);
    };
  }, [refresh]);

  // Identity-keyed subtree: the workspace state is not merely cleared, it is
  // unmounted, so no child component keeps an input buffer of the previous
  // student's private brief.
  return <AcademicWorkspace key={academicOwnerKey(principal)} principal={principal} initialState={initialState} />;
}

type AcademicWorkspaceProps = {
  principal: AcademicDraftPrincipal;
  initialState?: AcademicWorkspaceState;
};

export function AcademicWorkspace({ principal, initialState }: AcademicWorkspaceProps) {
  const { locale } = useLocale();
  const ta = useMemo(() => createAcademicTranslator(locale), [locale]);
  const labels = useMemo(() => createAcademicLabels(ta), [ta]);

  /**
   * The principal the in-memory state belongs to, plus whether the host seed has been
   * consumed for it. Nothing may be rendered for, read into, or persisted from this
   * instance unless it matches the current principal, so a reused instance cannot carry
   * one student's brief into another student's session. `AcademicResearchStudio`
   * additionally keys this subtree by the principal, so in the shipped route the state is
   * unmounted rather than merely reset; the fence below is what makes the guarantee hold
   * for any host that embeds the workspace directly.
   */
  const [owner, setOwner] = useState<AcademicDraftPrincipal>(principal);
  const [seedConsumed, setSeedConsumed] = useState(initialState !== undefined);
  const [state, setState] = useState<AcademicWorkspaceState>(() => initialState ?? createInitialWorkspaceState());
  const [draftStatus, setDraftStatus] = useState("");
  const sources = state.sources;

  /**
   * Live owner, as of the last commit. Handlers and deferred reads compare against it so
   * a callback captured under one identity cannot write under the next one. It is only
   * ever touched outside render, because render decisions belong to `owner` above.
   */
  const liveOwnerRef = useRef<AcademicDraftPrincipal>(principal);

  // Fail closed during render, before any commit: when the principal moves while this
  // instance stays mounted, the previous student's workspace is destroyed here rather
  // than painted and offered for saving. Setting state during render makes React re-run
  // this component immediately, so the stale value is never committed to the DOM, and the
  // host seed - which belongs to the identity that received it - is not carried over.
  if (owner !== principal) {
    setOwner(principal);
    setSeedConsumed(false);
    setState(createInitialWorkspaceState());
    setDraftStatus("");
  }

  useEffect(() => {
    liveOwnerRef.current = principal;
  }, [principal]);

  const stateBelongsToPrincipal = useCallback(
    () => liveOwnerRef.current === principal,
    [principal],
  );

  /**
   * Restore the identity-scoped draft after hydration so the first client render
   * matches the server render. Same shape as `ProductAdInputWizard`: the browser
   * read happens outside the committed render. It is additionally re-checked
   * against the owner the read was scheduled for, so one account's draft can
   * never land in another's session even if the identity moves inside this
   * microtask window. An absent draft leaves an empty workspace - never the
   * previous identity's data.
   */
  useEffect(() => {
    if (initialState && seedConsumed) return;
    const scheduledFor = principal;
    let cancelled = false;
    queueMicrotask(() => {
      if (cancelled || scheduledFor !== liveOwnerRef.current) return;
      const restored = readWorkspaceDraft(scheduledFor);
      if (restored && liveOwnerRef.current === scheduledFor) setState(restored);
    });
    return () => {
      cancelled = true;
    };
  }, [initialState, seedConsumed, principal]);

  const brief = state.brief;
  const issues = useMemo(() => validateAcademicBrief(brief), [brief]);
  const blocked = hasBlockingIssue(issues);
  const access = useMemo(() => countAccessLevels(sources), [sources]);

  /**
   * Single mutation entry point: it re-asserts that the value being written
   * belongs to the current principal, and refuses an edit whose closure was
   * captured under a different identity.
   */
  const mutate = useCallback((updater: (current: AcademicWorkspaceState) => AcademicWorkspaceState) => {
    if (liveOwnerRef.current !== principal) return;
    setState(updater);
  }, [principal]);

  /** Replacing the library also drops evidence notes that referenced removed records. */
  const setSources = useCallback((next: AcademicSource[]) => {
    mutate((current) => ({ ...current, sources: next, evidenceNotes: pruneEvidenceNotes(current.evidenceNotes, next) }));
  }, [mutate]);

  const updateBrief = useCallback((patch: Partial<AcademicBrief>) => {
    mutate((current) => {
      const nextBrief = { ...current.brief, ...patch };
      const chapters = patch.level && patch.level !== current.brief.level ? resizeChapterPlan(current.chapters, patch.level) : current.chapters;
      return { ...current, brief: nextBrief, chapters };
    });
  }, [mutate]);

  const updateRequirements = useCallback((patch: Partial<AcademicBrief["requirements"]>) => {
    mutate((current) => ({ ...current, brief: { ...current.brief, requirements: { ...current.brief.requirements, ...patch } } }));
  }, [mutate]);

  const applyDiscipline = useCallback(() => {
    if (!stateBelongsToPrincipal()) {
      setDraftStatus(ta("draft.identityChanged"));
      return;
    }
    mutate((current) => ({ ...current, brief: applyDisciplineDefaults(current.brief, current.brief.discipline) }));
    setDraftStatus(ta("plan.disciplineApplied"));
  }, [mutate, stateBelongsToPrincipal, ta]);

  function selectTab(tab: AcademicTab) {
    mutate((current) => ({ ...current, tab }));
  }

  const saveDraft = useCallback(() => {
    // Fail closed: state that is not provably this principal's is never written
    // under this principal's draft key.
    if (!stateBelongsToPrincipal()) {
      setDraftStatus(ta("draft.identityChanged"));
      return;
    }
    const saved = writeWorkspaceDraft(state, principal);
    setDraftStatus(saved ? ta("draft.saved") : ta("draft.unavailable"));
  }, [state, principal, stateBelongsToPrincipal, ta]);

  const resetDraft = useCallback(() => {
    if (!stateBelongsToPrincipal()) {
      setDraftStatus(ta("draft.identityChanged"));
      return;
    }
    clearWorkspaceDraft(principal);
    setState(createInitialWorkspaceState());
    setDraftStatus(ta("draft.cleared"));
  }, [principal, stateBelongsToPrincipal, ta]);

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
            mutate((current) => ({ ...current, evidenceNotes: setEvidenceNote(current.evidenceNotes, sourceId, questionIndex, text) }))
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
          onChange={(chapters) => mutate((current) => ({ ...current, chapters }))}
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
