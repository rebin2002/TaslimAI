"use client";
import { localeTag, type Locale } from "@/lib/i18n";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { Bell, Check, CheckCheck, LoaderCircle } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type NotificationItem, type NotificationList } from "@/lib/api";

const notificationLabels = {
  "generation.completed": "notification.generationCompleted",
  "generation.failed": "notification.generationFailed",
  "generation.attention": "notification.generationAttention",
  "billing.payment_failed": "notification.paymentFailed",
} as const;
const notificationUnreadEvent = "taslim:notification-unread-count";

type NotificationUnreadDetail = { workspaceId: string; unreadCount: number };

function formatNotificationTime(value: string, locale: Locale) {
  return new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function publishNotificationUnreadCount(workspaceId: string, unreadCount: number) {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent<NotificationUnreadDetail>(notificationUnreadEvent, { detail: { workspaceId, unreadCount } }));
  }
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
    const onUnreadCount = (event: Event) => {
      const detail = (event as CustomEvent<NotificationUnreadDetail>).detail;
      if (detail?.workspaceId === workspace.id && typeof detail.unreadCount === "number") {
        setUnreadCount(Math.max(0, detail.unreadCount));
      }
    };
    const refresh = async () => {
      try {
        const next = await api.getNotificationUnreadCount(workspace.id);
        if (active) setUnreadCount(next.unreadCount);
      } catch {
        if (active) setUnreadCount(0);
      }
    };
    window.addEventListener(notificationUnreadEvent, onUnreadCount);
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 15000);
    return () => {
      active = false;
      window.clearInterval(timer);
      window.removeEventListener(notificationUnreadEvent, onUnreadCount);
    };
  }, [workspace]);

  return unreadCount;
}

export function NotificationBell({ unreadCount }: Readonly<{ unreadCount: number }>) {
  const router = useRouter();
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<NotificationList | null>(null);
  const [loading, setLoading] = useState(false);
  const [workingId, setWorkingId] = useState<string | null>(null);
  const [panelError, setPanelError] = useState("");
  const panelId = `notification-panel-${useId().replaceAll(":", "")}`;
  const panelTitleId = `${panelId}-title`;
  const popoverRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const activeWorkspaceId = useRef(workspace?.id ?? null);
  if (activeWorkspaceId.current !== (workspace?.id ?? null)) activeWorkspaceId.current = workspace?.id ?? null;

  useEffect(() => {
    // Do not retain the previous workspace's panel or mutation state.
    /* eslint-disable react-hooks/set-state-in-effect */
    setResult(null);
    setOpen(false);
    setLoading(false);
    setWorkingId(null);
    setPanelError("");
    /* eslint-enable react-hooks/set-state-in-effect */
  }, [workspace?.id]);

  useEffect(() => {
    if (!open) return;
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(event.target as Node)) setOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      setOpen(false);
      window.requestAnimationFrame(() => triggerRef.current?.focus());
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [open]);

  async function openPanel() {
    const willOpen = !open;
    setOpen(willOpen);
    if (!willOpen || !workspace) return;
    const workspaceId = workspace.id;
    setLoading(true);
    setPanelError("");
    try {
      const next = await api.listNotifications(workspaceId, 1, 6);
      if (activeWorkspaceId.current !== workspaceId) return;
      setResult(next);
    } catch {
      if (activeWorkspaceId.current !== workspaceId) return;
      setResult(null);
      setPanelError(t("notification.loadError"));
    } finally {
      if (activeWorkspaceId.current === workspaceId) setLoading(false);
    }
  }

  async function markRead(item: NotificationItem): Promise<boolean> {
    if (!workspace || item.isRead) return true;
    const workspaceId = workspace.id;
    setWorkingId(item.id);
    setPanelError("");
    try {
      await api.markNotificationRead(workspaceId, item.id);
      if (activeWorkspaceId.current !== workspaceId) return false;
      const nextUnreadCount = Math.max(0, (result?.unreadCount ?? unreadCount) - 1);
      const readAt = new Date().toISOString();
      setResult((current) => current ? { ...current, unreadCount: Math.max(0, current.unreadCount - 1), items: current.items.map((entry) => entry.id === item.id ? { ...entry, isRead: true, readAt: entry.readAt ?? readAt } : entry) } : current);
      publishNotificationUnreadCount(workspaceId, nextUnreadCount);
      return true;
    } catch {
      if (activeWorkspaceId.current === workspaceId) setPanelError(t("notification.readError"));
      return false;
    } finally {
      if (activeWorkspaceId.current === workspaceId) setWorkingId(null);
    }
  }

  async function openNotification(item: NotificationItem, event: React.MouseEvent<HTMLAnchorElement>) {
    setOpen(false);
    if (item.isRead) return;
    event.preventDefault();
    if (await markRead(item)) router.push(item.destination);
  }

  async function markAllRead() {
    if (!workspace || !result?.unreadCount) return;
    const workspaceId = workspace.id;
    setWorkingId("all");
    setPanelError("");
    try {
      await api.markAllNotificationsRead(workspaceId);
      if (activeWorkspaceId.current !== workspaceId) return;
      const readAt = new Date().toISOString();
      setResult((current) => current ? { ...current, unreadCount: 0, items: current.items.map((item) => item.isRead ? item : { ...item, isRead: true, readAt: item.readAt ?? readAt }) } : current);
      publishNotificationUnreadCount(workspaceId, 0);
    } catch {
      if (activeWorkspaceId.current === workspaceId) setPanelError(t("notification.readError"));
    } finally {
      if (activeWorkspaceId.current === workspaceId) setWorkingId(null);
    }
  }

  const badgeCount = result?.unreadCount ?? unreadCount;
  const bellLabel = badgeCount > 0 ? `${t("navigation.notifications")}, ${t("notification.unread", { count: String(badgeCount) })}` : t("navigation.notifications");
  return <div className="notification-popover" ref={popoverRef}>
    <button ref={triggerRef} type="button" className="icon-button notification-button" aria-label={bellLabel} aria-haspopup="dialog" aria-expanded={open} aria-controls={panelId} onClick={() => void openPanel()}>
      <Bell size={18} aria-hidden="true" />
      {badgeCount > 0 && <span className="notification-badge" aria-hidden="true">{badgeCount > 99 ? "99+" : badgeCount}</span>}
    </button>
    {open && <section id={panelId} className="notification-panel" role="dialog" aria-modal="false" aria-label={t("notification.panelLabel")}>
      <div className="notification-panel-heading"><div><p className="section-eyebrow">{t("notification.eyebrow")}</p><h2 id={panelTitleId}>{t("notification.title")}</h2></div><button type="button" className="notification-mark-all" onClick={() => void markAllRead()} disabled={!result?.unreadCount || workingId === "all"} aria-busy={workingId === "all"}><CheckCheck size={14} aria-hidden="true" /> {t("notification.markAllRead")}</button></div>
      {panelError && <p className="notification-panel-error" role="alert">{panelError}</p>}
      {loading ? <div className="notification-panel-state" role="status" aria-live="polite"><LoaderCircle className="activity-spin" size={20} aria-hidden="true" /> {t("notification.loading")}</div> : !result?.items.length ? <div className="notification-panel-state"><Bell size={20} aria-hidden="true" /><span>{t("notification.empty")}</span></div> : <div className="notification-panel-list" role="list" aria-label={t("notification.title")}>{result.items.map((item) => <NotificationRow key={item.id} item={item} locale={locale} t={t} working={workingId === item.id} onRead={() => void markRead(item)} onNavigate={(event) => void openNotification(item, event)} />)}</div>}
      <Link href="/notifications" className="notification-panel-footer" onClick={() => setOpen(false)}>{t("notification.viewAll")} <span aria-hidden="true">→</span></Link>
    </section>}
  </div>;
}

function NotificationRow({ item, locale, t, working, onRead, onNavigate }: { item: NotificationItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onRead: () => void; onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  const label = t(notificationLabels[item.type]);
  const itemLabel = item.resourceTitle ? `${label}: ${item.resourceTitle}` : label;
  return <article className={`notification-row ${item.isRead ? "is-read" : "is-unread"}`} role="listitem" aria-label={itemLabel}>
    <Link href={item.destination} className="notification-row-link" onClick={onNavigate}>
      <span className="notification-row-dot" aria-hidden="true" />
      <span className="notification-row-copy"><strong>{label}</strong>{item.resourceTitle && <span>{item.resourceTitle}</span>}<time dateTime={item.createdAt}>{formatNotificationTime(item.createdAt, locale)}</time></span>
    </Link>
    {!item.isRead && <button type="button" className="notification-row-read" aria-label={`${t("notification.markRead")}: ${itemLabel}`} onClick={onRead} disabled={working} aria-busy={working}>{working ? <LoaderCircle className="activity-spin" size={14} aria-hidden="true" /> : <Check size={14} aria-hidden="true" />}</button>}
  </article>;
}

export const useActivityUnreadCount = useNotificationUnreadCount;
export const ActivityBell = NotificationBell;
