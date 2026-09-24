"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronDown, Search } from "lucide-react";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { BrandMark } from "@/components/BrandMark";
import { useAuth } from "@/components/AuthProvider";
import { NotificationBell } from "@/components/ActivityBell";
import { desktopNavigation, matchesNavigationPath } from "@/lib/navigation";

export function AppHeader({ unreadCount }: Readonly<{ unreadCount: number }>) {
  const pathname = usePathname();
  const { locale, setLocale, t } = useLocale();
  const { user } = useAuth();
  return (
    <header className="topbar app-header">
      <div className="topbar-inner">
        <BrandMark />
        <nav className="desktop-nav" aria-label={t("navigation.primary")}>
          {desktopNavigation.map((item) => {
            const Icon = item.icon;
            const active = item.href === "/create" ? pathname.startsWith("/create") : matchesNavigationPath(pathname, item.href);
            return (
              <Link key={item.href} href={item.href} className={`top-link ${active ? "is-active" : ""}`} aria-current={active ? "page" : undefined}>
                <Icon size={16} strokeWidth={1.8} />
                <span>{t(item.labelKey)}</span>
              </Link>
            );
          })}
        </nav>
        <div className="topbar-actions">
          <Link href="/search" className="icon-button search-button" aria-label={t("navigation.search")}>
            <Search size={18} />
          </Link>
          <NotificationBell unreadCount={unreadCount} />
          <label className="language-select">
            <span className="sr-only">{t("navigation.language")}</span>
            <select value={locale} onChange={(event) => setLocale(event.target.value as typeof locale)} aria-label={t("navigation.language")}>
              {locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}
            </select>
            <ChevronDown size={14} aria-hidden="true" />
          </label>
          {user ? <Link href="/account" className="profile-chip" aria-label={t("navigation.account")}>
            <span className="profile-avatar" aria-hidden="true">{user.displayName.slice(0, 1).toUpperCase()}</span>
            <span className="profile-name">{user.displayName}</span>
          </Link> : <Link href="/login" className="profile-chip auth-link">{t("auth.signIn")}</Link>}
        </div>
      </div>
    </header>
  );
}
