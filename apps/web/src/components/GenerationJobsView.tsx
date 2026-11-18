"use client";

import Link from "next/link";
import { CheckCircle2, Clock3, LoaderCircle, Play, XCircle } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob } from "@/lib/api";
import { canCancelGenerationJob, describeGenerationJobsSurface, isCurrentGenerationJobsAlert, isCurrentGenerationJobsWork, isJobReadableInWorkspace, isTerminalJob, mergeGenerationJobIntoRead, readGenerationJobs, resolveGenerationJobsDetail, type GenerationJobsAlert, type GenerationJobsRead, type GenerationJobsSelection, type GenerationJobsWork } from "@/lib/generationJobsState";
import { nextGenerationJobPollDelay } from "@/lib/generationJobsPolling";

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
  const detailId = `generation-job-detail-${job.id}`;

  return (
    <div id={detailId} className="generation-job-detail" aria-labelledby={detailTitleId}>
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
    <div className="generation-job-list" role="group" aria-label={t("jobs.recent")}>
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
            aria-controls={activeJobId === job.id ? `generation-job-detail-${job.id}` : undefined}
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
  // The session's personal workspace can be reassigned while this page stays mounted, so the whole
  // page is rendered from a read that carries the identity of what it read: the workspace id it
  // describes and the list attempt that produced it.
  const workspaceId = workspace?.id ?? null;
  const [read, setRead] = useState<GenerationJobsRead | null>(null);
  const [selection, setSelection] = useState<GenerationJobsSelection | null>(null);
  const [alert, setAlert] = useState<GenerationJobsAlert | null>(null);
  const [loading, setLoading] = useState(true);
  const [workingRequest, setWorkingRequest] = useState<GenerationJobsWork | null>(null);
  const [attemptEpoch, setAttemptEpoch] = useState(0);
  const requestGeneration = useRef(0);
  const selectionGeneration = useRef(0);
  const [pollRetryGeneration, setPollRetryGeneration] = useState(0);
  // Mirror the request generation in state for render-time busy checks: React refs
  // must not be read during render, and a workspace change invalidates stale work.
  const [liveRequestGeneration, setLiveRequestGeneration] = useState(0);
  // Derive busy synchronously: the old workspace must not keep new controls disabled
  // while a superseded create/cancel promise is still settling.
  const working = isCurrentGenerationJobsWork(workingRequest, workspaceId, liveRequestGeneration);
  const loadRecent = useCallback(async () => {
    if (!workspaceId) {
      // No workspace to read: retire the held read and everything built from it.
      requestGeneration.current += 1;
      setLiveRequestGeneration(requestGeneration.current);
      setAttemptEpoch(requestGeneration.current);
      setRead(null);
      setSelection(null);
      setAlert(null);
      setLoading(false);
      return;
    }
    const request = ++requestGeneration.current;
    setLiveRequestGeneration(request);
    setAttemptEpoch(request);
    setLoading(true);
    setAlert(null);
    try {
      const result = await api.listGenerationJobs(workspaceId);
      if (request !== requestGeneration.current) return;
      setRead({ workspaceId, epoch: request, jobs: result.items });
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      setAlert({ workspaceId, epoch: request, message: caught instanceof Error ? caught.message : t("jobs.loadError"), retryable: true });
    } finally {
      if (request === requestGeneration.current) setLoading(false);
    }
  }, [t, workspaceId]);
  // The protected page synchronizes its initial state with the server-side job list: the load is
  // started here, and its state is published only while it is still the newest read.
  useEffect(() => { void loadRecent(); }, [loadRecent]);
  // The detail panel, its cancel control and the poll timer are all derived from the current read.
  // Nothing the view renders or re-arms can be a record from a workspace it no longer serves, nor a
  // record remembered from a read of the same workspace that the session has since superseded -
  // the workspace fence alone cannot tell those two apart, the read identity can.
  const activeJob = resolveGenerationJobsDetail({ read, selection, activeWorkspaceId: workspaceId, attemptEpoch });
  useEffect(() => {
    const pollDelay = nextGenerationJobPollDelay(activeJob);
    if (!activeJob || pollDelay === null) return;
    let active = true;
    const jobId = activeJob.id;
    const epoch = requestGeneration.current;
    const poll = async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (!active) return;
        // The answer belongs to a read this page has already superseded: retire it with that read.
        if (epoch !== requestGeneration.current) return;
        setRead((previous) => mergeGenerationJobIntoRead(previous, next, workspaceId, attemptEpoch));
      } catch (caught) {
        if (active && workspaceId && epoch === requestGeneration.current) {
          setAlert({ workspaceId, epoch: attemptEpoch, message: caught instanceof Error ? caught.message : t("jobs.pollError"), retryable: true });
        }
      }
    };
    const timer = window.setTimeout(() => { void poll(); }, pollDelay);
    return () => { active = false; window.clearTimeout(timer); };
  }, [activeJob, attemptEpoch, pollRetryGeneration, t, workspaceId]);
  function selectJob(job: GenerationJob) {
    if (!workspaceId || !isJobReadableInWorkspace(job, workspaceId)) return;
    selectionGeneration.current += 1;
    setSelection({ workspaceId, jobId: job.id });
    setAlert(null);
  }
  function retryLoad() {
    if (activeJob && !isTerminalJob(activeJob)) {
      setPollRetryGeneration((value) => value + 1);
      setAlert(null);
      return;
    }
    void loadRecent();
  }
  async function createTestJob() {
    if (!workspaceId) return;
    // Starting a mutation supersedes the outstanding list request. Remember
    // whether we interrupted that read so its loading state cannot be stranded.
    const interruptedListRead = loading;
    const request = ++requestGeneration.current;
    setLiveRequestGeneration(request);
    const selection = selectionGeneration.current;
    setWorkingRequest({ workspaceId, request });
    setAlert(null);
    try {
      const job = await api.createGenerationJob(workspaceId, JSON.stringify({ purpose: "foundation-check" }), t("jobs.testTitle"));
      if (request !== requestGeneration.current) return;
      setRead((previous) => mergeGenerationJobIntoRead(previous, job, workspaceId, attemptEpoch));
      if (selection === selectionGeneration.current && isJobReadableInWorkspace(job, workspaceId)) setSelection({ workspaceId, jobId: job.id });
      // The first list response can no longer settle after this mutation bumped
      // the generation. Refresh its authoritative page so older history rows
      // are not lost behind the newly created job.
      if (interruptedListRead) void loadRecent();
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      // No list completion can clear loading now. Settle the view and allow the
      // normal Retry to load history, while keeping the Create error visible.
      if (interruptedListRead) setLoading(false);
      setAlert({ workspaceId, epoch: attemptEpoch, message: caught instanceof Error ? caught.message : t("jobs.createError"), retryable: interruptedListRead });
    } finally {
      // Retire only this action; a newer workspace's pending request owns its own indicator.
      setWorkingRequest((previous) => previous?.request === request && previous.workspaceId === workspaceId ? null : previous);
    }
  }
  async function cancelJob(job: GenerationJob) {
    // Fail closed before the mutation is issued: with no workspace bound to the page there is no
    // read this job could belong to.
    if (!workspaceId || !isJobReadableInWorkspace(job, workspaceId)) return;
    const request = ++requestGeneration.current;
    setLiveRequestGeneration(request);
    const selection = selectionGeneration.current;
    setWorkingRequest({ workspaceId, request });
    setAlert(null);
    try {
      await api.cancelGenerationJob(job.id);
      const next = await api.getGenerationJob(job.id);
      if (request !== requestGeneration.current) return;
      setRead((previous) => mergeGenerationJobIntoRead(previous, next, workspaceId, attemptEpoch));
      if (selection === selectionGeneration.current && isJobReadableInWorkspace(next, workspaceId)) setSelection({ workspaceId, jobId: next.id });
    } catch (caught) {
      if (request !== requestGeneration.current) return;
      setAlert({ workspaceId, epoch: attemptEpoch, message: caught instanceof Error ? caught.message : t("jobs.cancelError"), retryable: false });
    } finally {
      // Retire only this action; a newer workspace's pending request owns its own indicator.
      setWorkingRequest((previous) => previous?.request === request && previous.workspaceId === workspaceId ? null : previous);
    }
  }
  // Everything the page can say is derived from that one read, in the same render as the
  // AuthProvider workspace change. Waiting for loadRecent's effect would leave a retired read -
  // rows, titles, result payloads, a detail panel or a failure alert - on screen for a render.
  const visibleJobs = readGenerationJobs(read, workspaceId, attemptEpoch);
  const currentAlert = isCurrentGenerationJobsAlert(alert, workspaceId, attemptEpoch) ? alert : null;
  const surface = describeGenerationJobsSurface({ activeWorkspaceId: workspaceId, read, attemptEpoch, loading, alert: currentAlert });
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
        {currentAlert && <div className="form-error" role="alert" aria-atomic="true"><span>{currentAlert.message}</span></div>}
        {currentAlert?.retryable && <button type="button" className="secondary-button generation-retry-button" onClick={retryLoad} disabled={loading} aria-busy={loading}>{t("jobs.retry")}</button>}
        {activeJob && <GenerationJobDetail job={activeJob} t={t} canCancel={canCancel} working={working} onCancel={() => void cancelJob(activeJob)} />}
      </section>
      <section className="account-card generation-recent-card" aria-labelledby="generation-recent-title">
        <div className="card-title"><span className="card-title-icon"><Clock3 size={17} aria-hidden="true" /></span><div><h2 id="generation-recent-title">{t("jobs.recent")}</h2><p>{t("jobs.recentSubtitle")}</p></div></div>
        {surface === "loading" ? <div className="generation-empty" role="status" aria-live="polite">{t("jobs.loading")}</div> : surface === "empty" ? <div className="generation-empty" role="status" aria-live="polite">{t("jobs.empty")}</div> : <GenerationJobList jobs={visibleJobs} activeJobId={activeJob?.id ?? null} t={t} onSelect={selectJob} />}
      </section>
    </div>
  </div>;
}
