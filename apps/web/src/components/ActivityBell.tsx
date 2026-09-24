"use client";

import Link from "next/link";
import { Bell, Check, CheckCheck, LoaderCircle } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type NotificationItem, type NotificationList } from "@/lib/api";

const localeMap = { en: "en-US", ar: "ar", ku: "ku-Arab" } as const;
const notificationLabels = {
  "generation.completed": "notification.generationCompleted",
  "generation.failed": "notification.generationFailed",
  "generation.attention": "notification.generationAttention",
  "billing.payment_failed": "notification.paymentFailed",
} as const;

function formatNotificationTime(value: string, locale: keyof typeof localeMap) {
  return new Intl.DateTimeFormat(localeMap[locale], { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function useNotificationUnreadCount() {
  const { workspace } = useAuth();
  const [unreadCount, setUnreadCount] = useState(0);

  useEffect(() => {
    if (!workspace) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setUnreadCount(0);
      return;
    }
    let active = true;
    const refresh = async () => {
      try {
        const next = await api.getNotificationUnreadCount(workspace.id);
        if (active) setUnreadCount(next.unreadCount);
      } catch {
        if (active) setUnreadCount(0);
      }
    };
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 15000);
    return () => { active = false; window.clearInterval(timer); };
  }, [workspace]);

  return unreadCount;
}

export function NotificationBell({ unreadCount }: Readonly<{ unreadCount: number }>) {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<NotificationList | null>(null);
  const [loading, setLoading] = useState(false);
  const [workingId, setWorkingId] = useState<string | null>(null);
  const popoverRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", closeOnOutsideClick);
    return () => document.removeEventListener("mousedown", closeOnOutsideClick);
  }, [open]);

  async function openPanel() {
    const willOpen = !open;
    setOpen(willOpen);
    if (!willOpen || !workspace) return;
    setLoading(true);
    try {
      setResult(await api.listNotifications(workspace.id, 1, 6));
    } catch {
      setResult(null);
    } finally {
      setLoading(false);
    }
  }

  async function markRead(item: NotificationItem) {
    if (!workspace || item.isRead) return;
    setWorkingId(item.id);
    try {
      await api.markNotificationRead(workspace.id, item.id);
      setResult((current) => current ? { ...current, unreadCount: Math.max(0, current.unreadCount - 1), items: current.items.map((entry) => entry.id === item.id ? { ...entry, isRead: true, readAt: new Date().toISOString() } : entry) } : current);
    } finally {
      setWorkingId(null);
    }
  }

  async function markAllRead() {
    if (!workspace || !result?.unreadCount) return;
    setWorkingId("all");
    try {
      await api.markAllNotificationsRead(workspace.id);
      setResult((current) => current ? { ...current, unreadCount: 0, items: current.items.map((item) => ({ ...item, isRead: true, readAt: new Date().toISOString() })) } : current);
    } finally {
      setWorkingId(null);
    }
  }

  const badgeCount = result?.unreadCount ?? unreadCount;
  return <div className="notification-popover" ref={popoverRef}>
    <button type="button" className="icon-button notification-button" aria-label={t("navigation.notifications")} aria-expanded={open} onClick={() => void openPanel()}>
      <Bell size={18} />
      {badgeCount > 0 && <span className="notification-badge" aria-label={t("notification.unread", { count: String(badgeCount) })}>{badgeCount > 99 ? "99+" : badgeCount}</span>}
    </button>
    {open && <section className="notification-panel" aria-label={t("notification.panelLabel")}>
      <div className="notification-panel-heading"><div><p className="section-eyebrow">{t("notification.eyebrow")}</p><h2>{t("notification.title")}</h2></div><button type="button" className="notification-mark-all" onClick={() => void markAllRead()} disabled={!result?.unreadCount || workingId === "all"}><CheckCheck size={14} /> {t("notification.markAllRead")}</button></div>
      {loading ? <div className="notification-panel-state"><LoaderCircle className="activity-spin" size={20} /> {t("notification.loading")}</div> : !result?.items.length ? <div className="notification-panel-state"><Bell size={20} /><span>{t("notification.empty")}</span></div> : <div className="notification-panel-list">{result.items.map((item) => <NotificationRow key={item.id} item={item} locale={locale} t={t} working={workingId === item.id} onRead={() => void markRead(item)} onNavigate={() => { if (!item.isRead) void markRead(item); setOpen(false); }} />)}</div>}
      <Link href="/notifications" className="notification-panel-footer" onClick={() => setOpen(false)}>{t("notification.viewAll")} <span aria-hidden="true">→</span></Link>
    </section>}
  </div>;
}

function NotificationRow({ item, locale, t, working, onRead, onNavigate }: { item: NotificationItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onRead: () => void; onNavigate: () => void }) {
  const label = t(notificationLabels[item.type]);
  return <article className={`notification-row ${item.isRead ? "is-read" : "is-unread"}`}>
    <Link href={item.destination} className="notification-row-link" onClick={onNavigate}>
      <span className="notification-row-dot" aria-hidden="true" />
      <span className="notification-row-copy"><strong>{label}</strong>{item.resourceTitle && <span>{item.resourceTitle}</span>}<small>{formatNotificationTime(item.createdAt, locale)}</small></span>
    </Link>
    {!item.isRead && <button type="button" className="notification-row-read" aria-label={t("notification.markRead")} onClick={onRead} disabled={working}>{working ? <LoaderCircle className="activity-spin" size={14} /> : <Check size={14} />}</button>}
  </article>;
}

export const useActivityUnreadCount = useNotificationUnreadCount;
export const ActivityBell = NotificationBell;
