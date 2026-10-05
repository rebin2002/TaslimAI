"use client";

import Link from "next/link";
import { CheckCircle2, Clock3, LoaderCircle, Play, XCircle } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob } from "@/lib/api";
import { canCancelGenerationJob, isTerminalJob, mergeGenerationJob } from "@/lib/generationJobsState";

type Translate = (key: string, variables?: Record<string, string>) => string;

type GenerationJobDetailProps = Readonly<{
  job: GenerationJob;
  t: Translate;
  canCancel: boolean;
  working: boolean;
  onCancel: () => void;
}>;

export function clampGenerationProgress(progressPercent: number) {
  return Math.max(0, Math.min(100, progressPercent));
}

export function GenerationJobDetail({ job, t, canCancel, working, onCancel }: GenerationJobDetailProps) {
  const title = job.title ?? t("jobs.testTitle");
  const status = t(`jobs.status${job.status}`);
  const progress = clampGenerationProgress(job.progressPercent);
  const detailTitleId = `generation-job-detail-title-${job.id}`;

  return (
    <div className="generation-job-detail" aria-labelledby={detailTitleId}>
      <h3 id={detailTitleId} className="sr-only">{title}</h3>
      <div className="generation-job-detail-header">
        <div><span className="field-hint">{t("jobs.jobId")}</span><code>{job.id}</code></div>
        <span className={`generation-status generation-status-${job.status.toLowerCase()}`} aria-hidden="true">{status}</span>
      </div>
      <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {t("jobs.progressAnnouncement", { title, status, progress: String(progress) })}
      </p>
      <div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{progress}%</strong></div>
      <div
        className="generation-progress-track"
        role="progressbar"
        aria-label={t("jobs.progress")}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={progress}
        aria-valuetext={`${progress}%`}
      >
        <span style={{ width: `${progress}%` }} />
      </div>
      {job.resultJson && <div className="generation-result"><span className="field-hint">{t("jobs.result")}</span><pre>{job.resultJson}</pre></div>}
      {job.errorMessage && <div className="form-error" role="alert" aria-atomic="true"><XCircle size={15} aria-hidden="true" /> {job.errorMessage}</div>}
      {canCancel && <button type="button" className="secondary-button generation-cancel-button" onClick={onCancel} disabled={working} aria-busy={working}><XCircle size={15} aria-hidden="true" /> {t("jobs.cancel")}</button>}
      {job.status === "Succeeded" && <div className="form-success"><CheckCircle2 size={15} aria-hidden="true" /> {t("jobs.succeeded")}</div>}
      {job.status === "Running" && <div className="generation-running"><LoaderCircle size={14} aria-hidden="true" /> {t("jobs.running")}</div>}
    </div>
  );
}

type GenerationJobListProps = Readonly<{
  jobs: GenerationJob[];
  activeJobId: string | null;
  t: Translate;
  onSelect: (job: GenerationJob) => void;
}>;

export function GenerationJobList({ jobs, activeJobId, t, onSelect }: GenerationJobListProps) {
  return (
    <div className="generation-job-list">
      {jobs.map((job) => {
        const title = job.title ?? t("jobs.testTitle");
        const status = t(`jobs.status${job.status}`);
        const progress = clampGenerationProgress(job.progressPercent);
        return (
          <button
            type="button"
            className={`generation-job-list-item ${activeJobId === job.id ? "is-active" : ""}`}
            key={job.id}
            onClick={() => onSelect(job)}
            aria-pressed={activeJobId === job.id}
            aria-label={t("jobs.progressAnnouncement", { title, status, progress: String(progress) })}
          >
            <span><strong>{title}</strong><small>{progress}%</small></span>
            <span className={`generation-status generation-status-${job.status.toLowerCase()}`} aria-hidden="true">{status}</span>
          </button>
        );
      })}
    </div>
  );
}

export function GenerationJobsView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const [jobs, setJobs] = useState<GenerationJob[]>([]);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState("");
  const [canRetryLoad, setCanRetryLoad] = useState(false);
  const requestGeneration = useRef(0);
  const selectionGeneration = useRef(0);
  const [pollRetryGeneration, setPollRetryGeneration] = useState(0);

  const loadRecent = useCallback(async () => {
    if (!workspace) {
      requestGeneration.current += 1;
      setJobs([]);
      setCurrent(null);
      setLoading(false);
      return;
    }
    const request = ++requestGeneration.current;
    const workspaceId = workspace.id;
    setLoading(true);
    setError("");
    setCanRetryLoad(false);
    try {
      const result = await api.listGenerationJobs(workspaceId);
      if (request !== requestGeneration.current) return;
      setJobs(result.items);
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      setError(caught instanceof Error ? caught.message : t("jobs.loadError"));
      setCanRetryLoad(true);
    } finally {
      if (request === requestGeneration.current) setLoading(false);
    }
  }, [t, workspace]);

  // The protected page synchronizes its initial state with the server-side job list.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadRecent(); }, [loadRecent]);

  useEffect(() => {
    if (!current || isTerminalJob(current)) return;
    let active = true;
    const jobId = current.id;
    const poll = async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (!active) return;
        setCurrent(next);
        setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: next.id, error: "" }, next).jobs);
      } catch (caught) {
        if (active) {
          setError(caught instanceof Error ? caught.message : t("jobs.pollError"));
          setCanRetryLoad(true);
        }
      }
    };
    const timer = window.setTimeout(() => { void poll(); }, 500);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetryGeneration, t]);

  function selectJob(job: GenerationJob) {
    selectionGeneration.current += 1;
    setCurrent(job);
    setError("");
    setCanRetryLoad(false);
  }

  function retryLoad() {
    if (current && !isTerminalJob(current)) {
      setPollRetryGeneration((value) => value + 1);
      setCanRetryLoad(false);
      setError("");
      return;
    }
    void loadRecent();
  }

  async function createTestJob() {
    if (!workspace) return;
    const request = ++requestGeneration.current;
    const selection = selectionGeneration.current;
    const workspaceId = workspace.id;
    setWorking(true);
    setError("");
    setCanRetryLoad(false);
    try {
      const job = await api.createGenerationJob(workspaceId, JSON.stringify({ purpose: "foundation-check" }), t("jobs.testTitle"));
      if (request !== requestGeneration.current) return;
      setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: job.id, error: "" }, job).jobs);
      if (selection === selectionGeneration.current) setCurrent(job);
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      setError(caught instanceof Error ? caught.message : t("jobs.createError"));
    } finally {
      if (request === requestGeneration.current) setWorking(false);
    }
  }

  async function cancelJob(job: GenerationJob) {
    const request = ++requestGeneration.current;
    const selection = selectionGeneration.current;
    setWorking(true);
    setError("");
    setCanRetryLoad(false);
    try {
      await api.cancelGenerationJob(job.id);
      const next = await api.getGenerationJob(job.id);
      if (request !== requestGeneration.current) return;
      setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: next.id, error: "" }, next).jobs);
      if (selection === selectionGeneration.current) setCurrent(next);
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      setError(caught instanceof Error ? caught.message : t("jobs.cancelError"));
    } finally {
      if (request === requestGeneration.current) setWorking(false);
    }
  }

  const activeJob = current ?? jobs[0] ?? null;
  const canCancel = canCancelGenerationJob(activeJob);

  return <div className="account-page generation-jobs-page">
    <div className="account-header">
      <div><p className="section-eyebrow">{t("jobs.eyebrow")}</p><h1>{t("jobs.title")}</h1><p>{t("jobs.subtitle")}</p></div>
      <Link className="secondary-button" href="/account">{t("jobs.backAccount")}</Link>
    </div>
    <div className="generation-jobs-grid">
      <section className="account-card generation-job-card" aria-labelledby="generation-test-job-title">
        <div className="card-title"><span className="card-title-icon teal"><Play size={17} aria-hidden="true" /></span><div><h2 id="generation-test-job-title">{t("jobs.testTitle")}</h2><p>{t("jobs.testSubtitle")}</p></div></div>
        <button type="button" className="primary-button generation-create-button" onClick={() => void createTestJob()} disabled={working} aria-busy={working}><Play size={15} aria-hidden="true" /> {working ? t("jobs.working") : t("jobs.create")}</button>
        {error && <div className="form-error" role="alert" aria-atomic="true"><span>{error}</span></div>}
        {error && canRetryLoad && <button type="button" className="secondary-button generation-retry-button" onClick={retryLoad} disabled={loading} aria-busy={loading}>{t("jobs.retry")}</button>}
        {activeJob && <GenerationJobDetail job={activeJob} t={t} canCancel={canCancel} working={working} onCancel={() => void cancelJob(activeJob)} />}
      </section>
      <section className="account-card generation-recent-card" aria-labelledby="generation-recent-title">
        <div className="card-title"><span className="card-title-icon"><Clock3 size={17} aria-hidden="true" /></span><div><h2 id="generation-recent-title">{t("jobs.recent")}</h2><p>{t("jobs.recentSubtitle")}</p></div></div>
        {loading ? <div className="generation-empty" role="status" aria-live="polite">{t("jobs.loading")}</div> : jobs.length === 0 ? <div className="generation-empty" role="status" aria-live="polite">{t("jobs.empty")}</div> : <GenerationJobList jobs={jobs} activeJobId={activeJob?.id ?? null} t={t} onSelect={selectJob} />}
      </section>
    </div>
  </div>;
}
