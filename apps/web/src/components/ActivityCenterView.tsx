"use client";
import { localeTag, type Locale } from "@/lib/i18n";

import Link from "next/link";
import { Activity, CheckCheck, ExternalLink, LoaderCircle, XCircle } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type ActivityItem, type ActivityList } from "@/lib/api";
import { activityStatuses, applyActivityRead, applyAllActivityRead, type ActivityStatusFilter } from "@/lib/activityCenter";
import { isActivityRequestCurrent, startActivityRequest } from "@/lib/activityRequest";

function formatTime(value: string | null, locale: Locale, fallback: string) {
  if (!value) return fallback;
  return new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function ActivityCenterView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const [filter, setFilter] = useState<ActivityStatusFilter>("All");
  const [result, setResult] = useState<ActivityList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [workingId, setWorkingId] = useState<string | null>(null);
  const resultRef = useRef<ActivityList | null>(null);
  const requestSequence = useRef(0);
  const activeWorkspaceId = useRef(workspace?.id ?? null);
  if (activeWorkspaceId.current !== (workspace?.id ?? null)) activeWorkspaceId.current = workspace?.id ?? null;

  const load = useCallback(async (quiet = false) => {
    if (!workspace) return;
    const workspaceId = workspace.id;
    const request = startActivityRequest(requestSequence.current, workspaceId, filter);
    requestSequence.current = request.sequence;
    if (!quiet) setLoading(true);
    try {
      const next = await api.listActivity(workspaceId, 1, 50, filter);
      if (!isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) return;
      resultRef.current = next;
      setResult(next);
      setError("");
    } catch (caught) {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter) && !quiet) {
        setError(caught instanceof Error ? caught.message : t("activity.loadError"));
      }
    } finally {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter) && !quiet) setLoading(false);
    }
  }, [filter, t, workspace]);

  useEffect(() => {
    requestSequence.current += 1;
    resultRef.current = null;
    /* eslint-disable react-hooks/set-state-in-effect */
    setResult(null);
    setError("");
    setWorkingId(null);
    if (!activeWorkspaceId.current) setLoading(false);
    /* eslint-enable react-hooks/set-state-in-effect */
  }, [filter, workspace?.id]);

  // Synchronize the view with the activity API when the workspace or filter changes.
  useEffect(() => {
    // The loader synchronizes the component with the external activity API.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
    return () => { requestSequence.current += 1; };
  }, [load]);
  useEffect(() => {
    if (!workspace) return;
    const timer = window.setInterval(() => { void load(true); }, 5000);
    return () => window.clearInterval(timer);
  }, [load, workspace]);

  async function markRead(item: ActivityItem) {
    if (!workspace || item.isRead) return;
    const workspaceId = workspace.id;
    const request = startActivityRequest(requestSequence.current, workspaceId, filter);
    requestSequence.current = request.sequence;
    setWorkingId(item.jobId);
    try {
      await api.markActivityRead(workspaceId, item.jobId);
      if (!isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) return;
      const current = resultRef.current;
      if (current) {
        const update = applyActivityRead(current, item.jobId);
        resultRef.current = update.next;
        setResult(update.next);
      }
      setError("");
    } catch {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) setError(t("activity.readError"));
    } finally {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) setWorkingId(null);
    }
  }

  async function markAllRead() {
    if (!workspace || !result?.unreadCount) return;
    const workspaceId = workspace.id;
    const request = startActivityRequest(requestSequence.current, workspaceId, filter);
    requestSequence.current = request.sequence;
    setWorkingId("all");
    try {
      await api.markAllActivityRead(workspaceId);
      if (!isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) return;
      const current = resultRef.current;
      if (current) {
        const update = applyAllActivityRead(current);
        resultRef.current = update.next;
        setResult(update.next);
      }
      setError("");
    } catch {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) setError(t("activity.readError"));
    } finally {
      if (isActivityRequestCurrent(request, requestSequence.current, activeWorkspaceId.current, filter)) setWorkingId(null);
    }
  }

  const items = result?.items ?? [];
  return <div className="activity-page">
    <section className="activity-hero">
      <div className="section-heading"><div><p className="section-eyebrow">{t("activity.eyebrow")}</p><h1>{t("activity.title")}</h1><p>{t("activity.subtitle")}</p></div><span className="activity-hero-icon"><Activity size={24} /></span></div>
    </section>
    <div className="activity-toolbar">
      <div className="activity-tabs" role="tablist" aria-label={t("activity.filterLabel")}>{activityStatuses.map((status) => <button key={status} type="button" role="tab" aria-selected={filter === status} className={filter === status ? "is-active" : ""} onClick={() => setFilter(status)}>{status === "All" ? t("activity.all") : t(`activity.status.${status.toLowerCase()}`)}</button>)}</div>
      <button type="button" className="secondary-button activity-read-all" onClick={() => void markAllRead()} disabled={!result?.unreadCount || workingId === "all"}><CheckCheck size={15} /> {t("activity.markAllRead")}{result?.unreadCount ? <span className="activity-count">{result.unreadCount}</span> : null}</button>
    </div>
    {error && <div className="inline-error">{error}</div>}
    {loading ? <div className="activity-empty"><LoaderCircle className="activity-spin" size={24} /><p>{t("activity.loading")}</p></div> : !items.length ? <div className="activity-empty"><Activity size={28} /><h2>{t("activity.emptyTitle")}</h2><p>{t("activity.emptyDescription")}</p></div> : <div className="activity-list" aria-live="polite">{items.map((item) => <ActivityCard key={item.jobId} item={item} locale={locale} t={t} working={workingId === item.jobId} onRead={() => void markRead(item)} />)}</div>}
  </div>;
}

function ActivityCard({ item, locale, t, working, onRead }: { item: ActivityItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onRead: () => void }) {
  const terminal = item.status === "Completed" || item.status === "Failed" || item.status === "Cancelled";
  const assetHref = item.assetId ? `/assets?search=${encodeURIComponent(item.title)}` : null;
  return <article className={`activity-card ${item.isRead ? "is-read" : "is-unread"}`}>
    <div className="activity-card-mark" aria-hidden="true" />
    <div className="activity-card-icon"><Activity size={17} /></div>
    <div className="activity-card-main">
      <div className="activity-card-heading"><div><p className="activity-type">{t(`activity.type.${item.jobType}`)}</p><h2>{item.title}</h2></div><span className={`generation-status generation-status-${item.status.toLowerCase()}`}>{t(`activity.status.${item.status.toLowerCase()}`)}</span></div>
      <div className="activity-card-meta"><span>{t("activity.created", { time: formatTime(item.createdAt, locale, "") })}</span>{item.completedAt && <span>{t("activity.completedAt", { time: formatTime(item.completedAt, locale, "") })}</span>}</div>
      {!terminal && <><div className="generation-progress-label"><span>{t("activity.progress")}</span><strong>{item.progressPercent}%</strong></div><div className="generation-progress-track"><span style={{ width: `${item.progressPercent}%` }} /></div></>}
      {item.safeFailureMessage && <div className="activity-failure"><XCircle size={15} /> <span>{item.safeFailureMessage}</span></div>}
      <div className="activity-card-actions">{assetHref && <Link className="secondary-button" href={assetHref} onClick={onRead}><ExternalLink size={14} /> {t("activity.openAsset")}</Link>}{!item.isRead && <button type="button" className="activity-read-button" onClick={onRead} disabled={working}>{working ? t("activity.working") : t("activity.markRead")}</button>}</div>
    </div>
  </article>;
}
