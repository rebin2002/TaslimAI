"use client";
import { useNotificationUnreadCount } from "@/components/ActivityBell";
import { AppHeader } from "@/components/AppHeader";
import { MobileNav } from "@/components/MobileNav";
import { useLocale } from "@/components/LocaleProvider";

export function AppShell({ children }: Readonly<{ children: React.ReactNode }>) {
  const unreadCount = useNotificationUnreadCount();
  const { t } = useLocale();
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">{t("navigation.skipToContent")}</a>
      <AppHeader unreadCount={unreadCount} />
      <main id="main-content" className="page-content" tabIndex={-1}>{children}</main>
      <MobileNav unreadCount={unreadCount} />
    </div>
  );
}
