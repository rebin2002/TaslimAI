"use client";

import Link from "next/link";
import { Bell } from "lucide-react";
import { useEffect, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api } from "@/lib/api";

export function useActivityUnreadCount() {
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
        const next = await api.getActivityUnreadCount(workspace.id);
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

export function ActivityBell({ unreadCount }: Readonly<{ unreadCount: number }>) {
  const { t } = useLocale();

  return <Link href="/notifications" className="icon-button notification-button" aria-label={t("navigation.activity")}>
    <Bell size={18} />
    {unreadCount > 0 && <span className="notification-badge" aria-label={t("activity.unread", { count: String(unreadCount) })}>{unreadCount > 99 ? "99+" : unreadCount}</span>}
  </Link>;
}
