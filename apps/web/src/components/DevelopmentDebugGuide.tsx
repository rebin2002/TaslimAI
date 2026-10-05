"use client";

import Link from "next/link";
import { ArrowLeft, Bug, Check, ClipboardCheck, RotateCcw, ShieldCheck } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import {
  clearDevelopmentDebugState,
  developmentDebugProgress,
  emptyDevelopmentDebugState,
  loadDevelopmentDebugState,
  persistDevelopmentDebugState,
  type DevelopmentDebugState,
  type DevelopmentDebugStep,
} from "@/lib/developmentDebugState";
import styles from "./DevelopmentDebugGuide.module.css";

const stepDefinitions: Array<{ id: DevelopmentDebugStep; titleKey: string; descriptionKey: string }> = [
  { id: "reproduce", titleKey: "debugGuide.step.reproduce.title", descriptionKey: "debugGuide.step.reproduce.description" },
  { id: "compare", titleKey: "debugGuide.step.compare.title", descriptionKey: "debugGuide.step.compare.description" },
  { id: "boundary", titleKey: "debugGuide.step.boundary.title", descriptionKey: "debugGuide.step.boundary.description" },
  { id: "observe", titleKey: "debugGuide.step.observe.title", descriptionKey: "debugGuide.step.observe.description" },
  { id: "record", titleKey: "debugGuide.step.record.title", descriptionKey: "debugGuide.step.record.description" },
];

function browserStorage(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}

export function DevelopmentDebugGuide() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const workspaceId = workspace?.id ?? null;
  const [state, setState] = useState<DevelopmentDebugState>(emptyDevelopmentDebugState);
  const [loadedWorkspaceId, setLoadedWorkspaceId] = useState<string | null>(null);
  const progress = useMemo(() => developmentDebugProgress(state), [state]);
  const isReady = workspaceId !== null && loadedWorkspaceId === workspaceId;

  useEffect(() => {
    if (!workspaceId) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setState(emptyDevelopmentDebugState());
      setLoadedWorkspaceId(null);
      return;
    }
    setState(loadDevelopmentDebugState(workspaceId, browserStorage()));
    setLoadedWorkspaceId(workspaceId);
  }, [workspaceId]);

  useEffect(() => {
    if (!workspaceId || !isReady) return;
    persistDevelopmentDebugState(workspaceId, state, browserStorage());
  }, [isReady, state, workspaceId]);

  const updateStep = (step: DevelopmentDebugStep, checked: boolean) => {
    setState((current) => ({ ...current, completed: { ...current.completed, [step]: checked } }));
  };

  const clearGuide = () => {
    if (workspaceId) clearDevelopmentDebugState(workspaceId, browserStorage());
    setState(emptyDevelopmentDebugState());
  };

  return (
    <main className={styles.page} aria-labelledby="development-debug-title" aria-busy={!isReady}>
      <header className={styles.header}>
        <div className={styles.headerCopy}>
          <p className="section-eyebrow">{t("debugGuide.eyebrow")}</p>
          <h1 id="development-debug-title">{t("debugGuide.title")}</h1>
          <p>{t("debugGuide.subtitle")}</p>
        </div>
        <div className={styles.headerMark} aria-hidden="true"><Bug size={25} /></div>
      </header>

      <div className={styles.workspaceBanner} role="status">
        <ShieldCheck size={18} aria-hidden="true" />
        <span>{workspace ? t("debugGuide.workspace", { name: workspace.name }) : t("debugGuide.workspaceFallback")}</span>
      </div>

      <section className={styles.notice} aria-label={t("debugGuide.localOnly")}>
        <ClipboardCheck size={18} aria-hidden="true" />
        <p>{t("debugGuide.localOnly")}</p>
      </section>

      <section className={styles.progressCard} aria-labelledby="development-debug-progress-title">
        <div className={styles.progressHeading}>
          <div>
            <p className="section-eyebrow">{t("debugGuide.progressLabel")}</p>
            <h2 id="development-debug-progress-title">{t("debugGuide.progress", { completed: String(progress.completed), total: String(progress.total) })}</h2>
          </div>
          <span className={styles.progressIcon} aria-hidden="true"><Check size={17} /></span>
        </div>
        <div className={styles.progressTrack} aria-hidden="true"><span style={{ width: `${(progress.completed / progress.total) * 100}%` }} /></div>
      </section>

      <section className={styles.contentGrid}>
        <div className={styles.stepsCard} aria-labelledby="development-debug-steps-title">
          <div className={styles.sectionHeading}>
            <div>
              <p className="section-eyebrow">{t("debugGuide.stepsEyebrow")}</p>
              <h2 id="development-debug-steps-title">{t("debugGuide.stepsTitle")}</h2>
            </div>
            <span className={styles.stepCount}>{progress.completed}/{progress.total}</span>
          </div>
          <div className={styles.stepList}>
            {stepDefinitions.map((step, index) => (
              <label className={`${styles.step} ${state.completed[step.id] ? styles.stepCompleted : ""}`} key={step.id}>
                <input
                  type="checkbox"
                  checked={state.completed[step.id]}
                  disabled={!isReady}
                  onChange={(event) => updateStep(step.id, event.target.checked)}
                />
                <span className={styles.stepNumber} aria-hidden="true">{String(index + 1).padStart(2, "0")}</span>
                <span className={styles.stepCopy}>
                  <strong>{t(step.titleKey)}</strong>
                  <small>{t(step.descriptionKey)}</small>
                </span>
              </label>
            ))}
          </div>
        </div>

        <div className={styles.notesCard}>
          <label className={styles.notesLabel} htmlFor="development-debug-notes">
            <span className="section-eyebrow">{t("debugGuide.notesEyebrow")}</span>
            <strong>{t("debugGuide.notesTitle")}</strong>
            <small>{t("debugGuide.notesHint")}</small>
          </label>
          <textarea
            id="development-debug-notes"
            value={state.notes}
            disabled={!isReady}
            maxLength={4000}
            onChange={(event) => setState((current) => ({ ...current, notes: event.target.value }))}
            placeholder={t("debugGuide.notesPlaceholder")}
            aria-describedby="development-debug-notes-hint"
          />
          <span id="development-debug-notes-hint" className={styles.characterCount}>{state.notes.length}/4000</span>
          <button className={styles.clearButton} type="button" onClick={clearGuide} disabled={!isReady}>
            <RotateCcw size={14} aria-hidden="true" /> {t("debugGuide.clear")}
          </button>
        </div>
      </section>

      <footer className={styles.footerActions}>
        <Link href="/development" className="secondary-button"><ArrowLeft size={15} aria-hidden="true" /> {t("debugGuide.back")}</Link>
        <Link href="/" className="primary-button">{t("debugGuide.home")}</Link>
      </footer>
    </main>
  );
}
