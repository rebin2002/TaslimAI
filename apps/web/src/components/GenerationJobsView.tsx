"use client";

import Link from "next/link";
import { CheckCircle2, Clock3, LoaderCircle, Play, XCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob } from "@/lib/api";
import { canCancelGenerationJob, isTerminalJob, mergeGenerationJob } from "@/lib/generationJobsState";

export function GenerationJobsView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const [jobs, setJobs] = useState<GenerationJob[]>([]);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState("");

  const loadRecent = useCallback(async () => {
    if (!workspace) return;
    try {
      const result = await api.listGenerationJobs(workspace.id);
      setJobs(result.items);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("jobs.loadError"));
    } finally {
      setLoading(false);
    }
  }, [t, workspace]);

  // The protected page synchronizes its initial state with the server-side job list.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadRecent(); }, [loadRecent]);

  useEffect(() => {
    if (!current || isTerminalJob(current)) return;
    let active = true;
    const poll = async () => {
      try {
        const next = await api.getGenerationJob(current.id);
        if (!active) return;
        setCurrent(next);
        setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: next.id, error: "" }, next).jobs);
      } catch (caught) {
        if (active) setError(caught instanceof Error ? caught.message : t("jobs.pollError"));
      }
    };
    const timer = window.setTimeout(() => { void poll(); }, 500);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, t]);

  async function createTestJob() {
    if (!workspace) return;
    setWorking(true);
    setError("");
    try {
      const job = await api.createGenerationJob(workspace.id, JSON.stringify({ purpose: "foundation-check" }), t("jobs.testTitle"));
      setCurrent(job);
      setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: job.id, error: "" }, job).jobs);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("jobs.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancelJob(job: GenerationJob) {
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(job.id);
      const next = await api.getGenerationJob(job.id);
      setCurrent(next);
      setJobs((previous) => mergeGenerationJob({ jobs: previous, selectedJobId: next.id, error: "" }, next).jobs);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("jobs.cancelError"));
    } finally {
      setWorking(false);
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
      <section className="account-card generation-job-card">
        <div className="card-title"><span className="card-title-icon teal"><Play size={17} /></span><div><h2>{t("jobs.testTitle")}</h2><p>{t("jobs.testSubtitle")}</p></div></div>
        <button className="primary-button generation-create-button" onClick={() => void createTestJob()} disabled={working}><Play size={15} /> {working ? t("jobs.working") : t("jobs.create")}</button>
        {error && <div className="form-error">{error}</div>}
        {activeJob && <div className="generation-job-detail" aria-live="polite">
          <div className="generation-job-detail-header"><div><span className="field-hint">{t("jobs.jobId")}</span><code>{activeJob.id}</code></div><span className={`generation-status generation-status-${activeJob.status.toLowerCase()}`}>{t(`jobs.status${activeJob.status}`)}</span></div>
          <div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{activeJob.progressPercent}%</strong></div>
          <div className="generation-progress-track"><span style={{ width: `${activeJob.progressPercent}%` }} /></div>
          {activeJob.resultJson && <div className="generation-result"><span className="field-hint">{t("jobs.result")}</span><pre>{activeJob.resultJson}</pre></div>}
          {activeJob.errorMessage && <div className="form-error"><XCircle size={15} /> {activeJob.errorMessage}</div>}
          {canCancel && <button className="secondary-button generation-cancel-button" onClick={() => void cancelJob(activeJob)} disabled={working}><XCircle size={15} /> {t("jobs.cancel")}</button>}
          {activeJob.status === "Succeeded" && <div className="form-success"><CheckCircle2 size={15} /> {t("jobs.succeeded")}</div>}
          {activeJob.status === "Running" && <div className="generation-running"><LoaderCircle size={14} /> {t("jobs.running")}</div>}
        </div>}
      </section>
      <section className="account-card generation-recent-card">
        <div className="card-title"><span className="card-title-icon"><Clock3 size={17} /></span><div><h2>{t("jobs.recent")}</h2><p>{t("jobs.recentSubtitle")}</p></div></div>
        {loading ? <div className="generation-empty">{t("jobs.loading")}</div> : jobs.length === 0 ? <div className="generation-empty">{t("jobs.empty")}</div> : <div className="generation-job-list">{jobs.map((job) => <button className={`generation-job-list-item ${activeJob?.id === job.id ? "is-active" : ""}`} key={job.id} onClick={() => setCurrent(job)}><span><strong>{job.title ?? t("jobs.testTitle")}</strong><small>{job.id.slice(0, 8)} · {job.progressPercent}%</small></span><span className={`generation-status generation-status-${job.status.toLowerCase()}`}>{t(`jobs.status${job.status}`)}</span></button>)}</div>}
      </section>
    </div>
  </div>;
}
