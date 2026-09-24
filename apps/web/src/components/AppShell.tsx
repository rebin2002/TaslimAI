"use client";
import { useNotificationUnreadCount } from "@/components/ActivityBell";
import { AppHeader } from "@/components/AppHeader";
import { MobileNav } from "@/components/MobileNav";

export function AppShell({ children }: Readonly<{ children: React.ReactNode }>) {
  const unreadCount = useNotificationUnreadCount();
  return (
    <div className="app-shell">
      <AppHeader unreadCount={unreadCount} />
      <main className="page-content">{children}</main>
      <MobileNav unreadCount={unreadCount} />
    </div>
  );
}
