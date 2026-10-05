"use client";
import { localeTag, type Locale } from "@/lib/i18n";

import Link from "next/link";
import { Bell, Check, CheckCheck, ExternalLink, LoaderCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type NotificationItem, type NotificationList } from "@/lib/api";
import { publishNotificationUnreadCount } from "@/components/ActivityBell";

const notificationLabels = {
  "generation.completed": "notification.generationCompleted",
  "generation.failed": "notification.generationFailed",
  "generation.attention": "notification.generationAttention",
  "billing.payment_failed": "notification.paymentFailed",
} as const;

function formatTime(value: string, locale: Locale) {
  return new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function NotificationCenterView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const [result, setResult] = useState<NotificationList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [workingId, setWorkingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!workspace) return;
    setLoading(true);
    try {
      setResult(await api.listNotifications(workspace.id, 1, 50));
      setError("");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("notification.loadError"));
    } finally {
      setLoading(false);
    }
  }, [t, workspace]);

  // Synchronize notifications whenever the active workspace changes.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void load(); }, [load]);

  async function markRead(item: NotificationItem) {
    if (!workspace || item.isRead) return;
    setWorkingId(item.id);
    try {
      await api.markNotificationRead(workspace.id, item.id);
      const nextUnreadCount = Math.max(0, (result?.unreadCount ?? 0) - 1);
      const readAt = new Date().toISOString();
      setResult((current) => current ? { ...current, unreadCount: Math.max(0, current.unreadCount - 1), items: current.items.map((entry) => entry.id === item.id ? { ...entry, isRead: true, readAt: entry.readAt ?? readAt } : entry) } : current);
      setError("");
      publishNotificationUnreadCount(workspace.id, nextUnreadCount);
    } catch {
      setError(t("notification.readError"));
    } finally {
      setWorkingId(null);
    }
  }

  async function markAllRead() {
    if (!workspace || !result?.unreadCount) return;
    setWorkingId("all");
    try {
      await api.markAllNotificationsRead(workspace.id);
      const readAt = new Date().toISOString();
      setResult((current) => current ? { ...current, unreadCount: 0, items: current.items.map((item) => item.isRead ? item : { ...item, isRead: true, readAt: item.readAt ?? readAt }) } : current);
      setError("");
      publishNotificationUnreadCount(workspace.id, 0);
    } catch {
      setError(t("notification.readError"));
    } finally {
      setWorkingId(null);
    }
  }

  const items = result?.items ?? [];
  return <div className="notification-page" aria-labelledby="notifications-page-title">
    <section className="notification-hero" aria-labelledby="notifications-page-title">
      <div className="section-heading"><div><p className="section-eyebrow">{t("notification.eyebrow")}</p><h1 id="notifications-page-title">{t("notification.title")}</h1><p>{t("notification.subtitle")}</p></div><span className="notification-hero-icon" aria-hidden="true"><Bell size={24} /></span></div>
      <div className="notification-center-links"><Link href="/activity" className="secondary-button">{t("notification.viewActivity")} <ExternalLink size={14} aria-hidden="true" /></Link><button type="button" className="secondary-button" onClick={() => void markAllRead()} disabled={!result?.unreadCount || workingId === "all"} aria-busy={workingId === "all"}><CheckCheck size={15} aria-hidden="true" /> {t("notification.markAllRead")}{result?.unreadCount ? <span className="activity-count" aria-hidden="true">{result.unreadCount}</span> : null}</button></div>
    </section>
    {error && <div className="inline-error" role="alert" aria-live="assertive">{error}</div>}
    {loading ? <div className="notification-empty" role="status" aria-live="polite"><LoaderCircle className="activity-spin" size={24} aria-hidden="true" /><p>{t("notification.loading")}</p></div> : !items.length ? <div className="notification-empty"><Bell size={28} aria-hidden="true" /><h2>{t("notification.emptyTitle")}</h2><p>{t("notification.emptyDescription")}</p></div> : <div className="notification-center-list" role="list" aria-label={t("notification.title")} aria-live="polite">{items.map((item) => <NotificationCard key={item.id} item={item} locale={locale} t={t} working={workingId === item.id} onRead={() => void markRead(item)} />)}</div>}
  </div>;
}

function NotificationCard({ item, locale, t, working, onRead }: { item: NotificationItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onRead: () => void }) {
  const label = t(notificationLabels[item.type]);
  const itemLabel = item.resourceTitle ? `${label}: ${item.resourceTitle}` : label;
  return <article className={`notification-card ${item.isRead ? "is-read" : "is-unread"}`} role="listitem" aria-label={itemLabel}>
    <div className="notification-card-mark" aria-hidden="true" />
    <div className="notification-card-icon" aria-hidden="true"><Bell size={17} /></div>
    <div className="notification-card-main"><div className="notification-card-heading"><div><p className="notification-type">{label}</p>{item.resourceTitle && <h2>{item.resourceTitle}</h2>}</div>{!item.isRead && <span className="notification-new-label">{t("notification.new")}</span>}</div><div className="notification-card-meta"><time dateTime={item.createdAt}>{formatTime(item.createdAt, locale)}</time></div><div className="notification-card-actions"><Link className="secondary-button" href={item.destination} onClick={onRead}><ExternalLink size={14} aria-hidden="true" /> {t("notification.open")}</Link>{!item.isRead && <button type="button" className="notification-read-button" onClick={onRead} disabled={working} aria-label={`${t("notification.markRead")}: ${itemLabel}`} aria-busy={working}>{working ? <LoaderCircle className="activity-spin" size={14} aria-hidden="true" /> : <Check size={14} aria-hidden="true" />} {working ? t("notification.working") : t("notification.markRead")}</button>}</div></div>
  </article>;
}
