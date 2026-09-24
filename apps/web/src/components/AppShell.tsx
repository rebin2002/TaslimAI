"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronDown, Search } from "lucide-react";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { BrandMark } from "@/components/BrandMark";
import { useAuth } from "@/components/AuthProvider";
import { NotificationBell, useNotificationUnreadCount } from "@/components/ActivityBell";
import { desktopNavigation, matchesNavigationPath, primaryNavigation } from "@/lib/navigation";

export function AppShell({ children }: Readonly<{ children: React.ReactNode }>) {
  const pathname = usePathname();
  const { locale, setLocale, t } = useLocale();
  const { user } = useAuth();
  const unreadCount = useNotificationUnreadCount();
  const activePath = primaryNavigation.find((item) => matchesNavigationPath(pathname, item.href))?.href ?? null;

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="topbar-inner">
          <BrandMark />
          <nav className="desktop-nav" aria-label={t("navigation.primary")}>
            {desktopNavigation.map((item) => {
              const Icon = item.icon;
              const active = item.href === "/create" ? pathname.startsWith("/create") : matchesNavigationPath(pathname, item.href);
              return (
                <Link key={item.href} href={item.href} className={`top-link ${active ? "is-active" : ""}`}>
                  <Icon size={16} strokeWidth={1.8} />
                  {t(item.labelKey)}
                </Link>
              );
            })}
          </nav>
          <div className="topbar-actions">
            <button type="button" className="icon-button search-button" aria-label={t("navigation.search")}>
              <Search size={18} />
            </button>
            <NotificationBell unreadCount={unreadCount} />
            <label className="language-select">
              <span className="sr-only">{t("navigation.language")}</span>
              <select value={locale} onChange={(event) => setLocale(event.target.value as typeof locale)}>
                {locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}
              </select>
              <ChevronDown size={14} aria-hidden="true" />
            </label>
            {user ? <Link href="/account" className="profile-chip" aria-label={t("navigation.account")}>
              <span className="profile-avatar">{user.displayName.slice(0, 1).toUpperCase()}</span>
              <span className="profile-name">{user.displayName}</span>
            </Link> : <Link href="/login" className="profile-chip auth-link">{t("auth.signIn")}</Link>}
          </div>
        </div>
      </header>
      <main className="page-content">{children}</main>
      <nav className="mobile-bottom-nav" aria-label={t("navigation.mobilePrimary")}>
        {primaryNavigation.map((item) => {
          const Icon = item.icon;
          const active = activePath === item.href || (item.href === "/create" && pathname.startsWith("/create"));
              const isNotification = item.href === "/notifications";
              return (
            <Link key={item.href} href={item.href} className={`mobile-nav-link ${active ? "is-active" : ""}`} aria-current={active ? "page" : undefined}>
              <span className="mobile-nav-icon"><Icon size={19} strokeWidth={active ? 2.2 : 1.8} />{isNotification && unreadCount > 0 && <span className="mobile-activity-badge" aria-label={t("notification.unread", { count: String(unreadCount) })}>{unreadCount > 99 ? "99+" : unreadCount}</span>}</span>
              <span>{t(item.labelKey)}</span>
            </Link>
          );
        })}
      </nav>
    </div>
  );
}
