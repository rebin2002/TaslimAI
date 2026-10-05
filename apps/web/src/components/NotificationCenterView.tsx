"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { Bell, Check, CheckCheck, ChevronLeft, ChevronRight, ExternalLink, LoaderCircle } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type NotificationItem, type NotificationList } from "@/lib/api";
import { publishNotificationUnreadCount } from "@/components/ActivityBell";
import { applyAllNotificationsRead, applyNotificationRead } from "@/lib/notificationReadState";

const localeMap = { en: "en-US", ar: "ar", ku: "ku-Arab" } as const;
const notificationPageSize = 20;
const notificationLabels = {
  "generation.completed": "notification.generationCompleted",
  "generation.failed": "notification.generationFailed",
  "generation.attention": "notification.generationAttention",
  "billing.payment_failed": "notification.paymentFailed",
} as const;

function formatTime(value: string, locale: keyof typeof localeMap) {
  return new Intl.DateTimeFormat(localeMap[locale], { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function NotificationCenterView() {
  const router = useRouter();
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const workspaceId = workspace?.id ?? null;
  const activeWorkspaceId = useRef(workspaceId);
  const requestGeneration = useRef(0);
  const [result, setResult] = useState<NotificationList | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [workingId, setWorkingId] = useState<string | null>(null);
  const resultRef = useRef<NotificationList | null>(null);

  // Keep late responses and mutations from an old workspace from repainting the active one.
  if (activeWorkspaceId.current !== workspaceId) activeWorkspaceId.current = workspaceId;

  const load = useCallback(async (generation: number) => {
    if (!workspaceId) {
      resultRef.current = null;
      setResult(null);
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      const next = await api.listNotifications(workspaceId, page, notificationPageSize);
      if (generation !== requestGeneration.current || activeWorkspaceId.current !== workspaceId) return;
      resultRef.current = next;
      setResult(next);
      setError("");
    } catch (caught) {
      if (generation !== requestGeneration.current || activeWorkspaceId.current !== workspaceId) return;
      setError(caught instanceof Error ? caught.message : t("notification.loadError"));
    } finally {
      if (generation === requestGeneration.current && activeWorkspaceId.current === workspaceId) setLoading(false);
    }
  }, [page, t, workspaceId]);

  useEffect(() => {
    // Workspace changes must clear the previous tenant's records before the next response arrives.
    /* eslint-disable react-hooks/set-state-in-effect */
    resultRef.current = null;
    setResult(null);
    setError("");
    setPage(1);
    setWorkingId(null);
    /* eslint-enable react-hooks/set-state-in-effect */
  }, [workspaceId]);

  useEffect(() => {
    const generation = ++requestGeneration.current;
    // The loader owns loading/error state and ignores stale responses.
    void load(generation);
    return () => {
      if (requestGeneration.current === generation) requestGeneration.current += 1;
    };
  }, [load]);

  async function markRead(item: NotificationItem): Promise<boolean> {
    if (!workspaceId || item.isRead) return true;
    setWorkingId(item.id);
    try {
      await api.markNotificationRead(workspaceId, item.id);
      if (activeWorkspaceId.current !== workspaceId) return false;
      const readAt = new Date().toISOString();
      const current = resultRef.current;
      if (current) {
        const update = applyNotificationRead(current, item.id, readAt);
        resultRef.current = update.next;
        setResult(update.next);
        if (update.unreadCountChanged) publishNotificationUnreadCount(workspaceId, update.next.unreadCount);
      }
      setError("");
      return true;
    } catch {
      if (activeWorkspaceId.current === workspaceId) setError(t("notification.readError"));
      return false;
    } finally {
      if (activeWorkspaceId.current === workspaceId) setWorkingId(null);
    }
  }

  async function openNotification(item: NotificationItem, event: React.MouseEvent<HTMLAnchorElement>) {
    if (item.isRead) return;
    event.preventDefault();
    if (await markRead(item)) router.push(item.destination);
  }

  async function markAllRead() {
    if (!workspaceId || !result?.unreadCount) return;
    setWorkingId("all");
    try {
      await api.markAllNotificationsRead(workspaceId);
      if (activeWorkspaceId.current !== workspaceId) return;
      const readAt = new Date().toISOString();
      const current = resultRef.current;
      if (current) {
        const update = applyAllNotificationsRead(current, readAt);
        resultRef.current = update.next;
        setResult(update.next);
        if (update.unreadCountChanged) publishNotificationUnreadCount(workspaceId, 0);
      }
      setError("");
    } catch {
      if (activeWorkspaceId.current === workspaceId) setError(t("notification.readError"));
    } finally {
      if (activeWorkspaceId.current === workspaceId) setWorkingId(null);
    }
  }

  const items = result?.items ?? [];
  return <div className="notification-page" aria-labelledby="notifications-page-title">
    <section className="notification-hero" aria-labelledby="notifications-page-title">
      <div className="section-heading"><div><p className="section-eyebrow">{t("notification.eyebrow")}</p><h1 id="notifications-page-title">{t("notification.title")}</h1><p>{t("notification.subtitle")}</p></div><span className="notification-hero-icon" aria-hidden="true"><Bell size={24} /></span></div>
      <div className="notification-center-links"><Link href="/activity" className="secondary-button">{t("notification.viewActivity")} <ExternalLink size={14} aria-hidden="true" /></Link><button type="button" className="secondary-button" onClick={() => void markAllRead()} disabled={!result?.unreadCount || workingId === "all"} aria-busy={workingId === "all"}><CheckCheck size={15} aria-hidden="true" /> {t("notification.markAllRead")}{result?.unreadCount ? <span className="activity-count" aria-hidden="true">{result.unreadCount}</span> : null}</button></div>
    </section>
    {error && <div className="inline-error" role="alert" aria-live="assertive">{error}</div>}
    {loading ? <div className="notification-empty" role="status" aria-live="polite"><LoaderCircle className="activity-spin" size={24} aria-hidden="true" /><p>{t("notification.loading")}</p></div> : !items.length ? <div className="notification-empty"><Bell size={28} aria-hidden="true" /><h2>{t("notification.emptyTitle")}</h2><p>{t("notification.emptyDescription")}</p></div> : <div className="notification-center-list" role="list" aria-label={t("notification.title")} aria-live="polite">{items.map((item) => <NotificationCard key={item.id} item={item} locale={locale} t={t} working={workingId === item.id} onRead={() => void markRead(item)} onOpen={(event) => void openNotification(item, event)} />)}</div>}
    {result && result.totalPages > 1 && <nav className="asset-pagination" aria-label={t("notification.title")}><button type="button" className="secondary-button" disabled={loading || page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}><ChevronLeft size={14} aria-hidden="true" /> {t("assets.previous")}</button><span aria-live="polite">{t("assets.page", { page: String(result.page), total: String(result.totalPages) })}</span><button type="button" className="secondary-button" disabled={loading || page >= result.totalPages} onClick={() => setPage((value) => Math.min(result.totalPages, value + 1))}>{t("assets.next")} <ChevronRight size={14} aria-hidden="true" /></button></nav>}
  </div>;
}

function NotificationCard({ item, locale, t, working, onRead, onOpen }: { item: NotificationItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onRead: () => void; onOpen: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  const label = t(notificationLabels[item.type]);
  const itemLabel = item.resourceTitle ? `${label}: ${item.resourceTitle}` : label;
  return <article className={`notification-card ${item.isRead ? "is-read" : "is-unread"}`} role="listitem" aria-label={itemLabel}>
    <div className="notification-card-mark" aria-hidden="true" />
    <div className="notification-card-icon" aria-hidden="true"><Bell size={17} /></div>
    <div className="notification-card-main"><div className="notification-card-heading"><div><p className="notification-type">{label}</p>{item.resourceTitle && <h2>{item.resourceTitle}</h2>}</div>{!item.isRead && <span className="notification-new-label">{t("notification.new")}</span>}</div><div className="notification-card-meta"><time dateTime={item.createdAt}>{formatTime(item.createdAt, locale)}</time></div><div className="notification-card-actions"><Link className="secondary-button" href={item.destination} onClick={onOpen}><ExternalLink size={14} aria-hidden="true" /> {t("notification.open")}</Link>{!item.isRead && <button type="button" className="notification-read-button" onClick={onRead} disabled={working} aria-label={`${t("notification.markRead")}: ${itemLabel}`} aria-busy={working}>{working ? <LoaderCircle className="activity-spin" size={14} aria-hidden="true" /> : <Check size={14} aria-hidden="true" />} {working ? t("notification.working") : t("notification.markRead")}</button>}</div></div>
  </article>;
}
