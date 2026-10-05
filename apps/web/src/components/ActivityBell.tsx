"use client";
import { localeTag, type Locale } from "@/lib/i18n";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { Bell, Check, CheckCheck, LoaderCircle } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type NotificationItem, type NotificationList } from "@/lib/api";
import { isNotificationRequestCurrent, startNotificationRequest } from "@/lib/notificationRequest";
import { applyAllNotificationsRead, applyNotificationRead } from "@/lib/notificationReadState";

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
  const requestSequence = useRef(0);

  useEffect(() => {
    if (!workspace) {
      requestSequence.current += 1;
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setUnreadCount(0);
      return;
    }
    const workspaceId = workspace.id;
    let active = true;
    const onUnreadCount = (event: Event) => {
      const detail = (event as CustomEvent<NotificationUnreadDetail>).detail;
      if (detail?.workspaceId === workspaceId && typeof detail.unreadCount === "number") {
        setUnreadCount(Math.max(0, detail.unreadCount));
      }
    };
    const refresh = async () => {
      const request = startNotificationRequest(requestSequence.current, workspaceId);
      requestSequence.current = request.sequence;
      try {
        const next = await api.getNotificationUnreadCount(workspaceId);
        if (active && isNotificationRequestCurrent(request, requestSequence.current, workspaceId)) setUnreadCount(next.unreadCount);
      } catch {
        if (active && isNotificationRequestCurrent(request, requestSequence.current, workspaceId)) setUnreadCount(0);
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
  const resultRef = useRef<NotificationList | null>(null);
  const panelId = `notification-panel-${useId().replaceAll(":", "")}`;
  const panelTitleId = `${panelId}-title`;
  const popoverRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRequestSequence = useRef(0);
  const activeWorkspaceId = useRef(workspace?.id ?? null);
  if (activeWorkspaceId.current !== (workspace?.id ?? null)) activeWorkspaceId.current = workspace?.id ?? null;

  useEffect(() => {
    panelRequestSequence.current += 1;
    // Workspace changes replace the panel's tenant-scoped state before the next render.
    /* eslint-disable react-hooks/set-state-in-effect */
    resultRef.current = null;
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
    if (!willOpen || !workspace) {
      panelRequestSequence.current += 1;
      setLoading(false);
      setWorkingId(null);
      setPanelError("");
      return;
    }
    const workspaceId = workspace.id;
    const request = startNotificationRequest(panelRequestSequence.current, workspaceId);
    panelRequestSequence.current = request.sequence;
    setLoading(true);
    setPanelError("");
    try {
      const next = await api.listNotifications(workspaceId, 1, 6);
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) {
        resultRef.current = next;
        setResult(next);
      }
    } catch {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) {
        resultRef.current = null;
        setResult(null);
        setPanelError(t("notification.loadError"));
      }
    } finally {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) setLoading(false);
    }
  }

  async function markRead(item: NotificationItem): Promise<boolean> {
    if (!workspace || item.isRead) return true;
    const workspaceId = workspace.id;
    const request = startNotificationRequest(panelRequestSequence.current, workspaceId);
    panelRequestSequence.current = request.sequence;
    setWorkingId(item.id);
    setPanelError("");
    try {
      await api.markNotificationRead(workspaceId, item.id);
      if (!isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) return false;
      const readAt = new Date().toISOString();
      const current = resultRef.current;
      if (current) {
        const update = applyNotificationRead(current, item.id, readAt);
        resultRef.current = update.next;
        setResult(update.next);
        if (update.unreadCountChanged) publishNotificationUnreadCount(workspaceId, update.next.unreadCount);
      }
      return true;
    } catch {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) setPanelError(t("notification.readError"));
      return false;
    } finally {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) setWorkingId(null);
    }
  }

  async function openNotification(item: NotificationItem, event: React.MouseEvent<HTMLAnchorElement>) {
    if (item.isRead) {
      setOpen(false);
      return;
    }
    event.preventDefault();
    if (await markRead(item)) {
      setOpen(false);
      router.push(item.destination);
    }
  }

  async function markAllRead() {
    if (!workspace || !result?.unreadCount) return;
    const workspaceId = workspace.id;
    const request = startNotificationRequest(panelRequestSequence.current, workspaceId);
    panelRequestSequence.current = request.sequence;
    setWorkingId("all");
    setPanelError("");
    try {
      await api.markAllNotificationsRead(workspaceId);
      if (!isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) return;
      const readAt = new Date().toISOString();
      const current = resultRef.current;
      if (current) {
        const update = applyAllNotificationsRead(current, readAt);
        resultRef.current = update.next;
        setResult(update.next);
        if (update.unreadCountChanged) publishNotificationUnreadCount(workspaceId, update.next.unreadCount);
      }
    } catch {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) setPanelError(t("notification.readError"));
    } finally {
      if (isNotificationRequestCurrent(request, panelRequestSequence.current, activeWorkspaceId.current)) setWorkingId(null);
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
