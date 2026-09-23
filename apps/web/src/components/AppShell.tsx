"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Bell, ChevronDown, Plus, Search } from "lucide-react";
import { navigation } from "@/lib/data";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { BrandMark } from "@/components/BrandMark";
import { useAuth } from "@/components/AuthProvider";

export function AppShell({ children }: Readonly<{ children: React.ReactNode }>) {
  const pathname = usePathname();
  const { locale, setLocale, t } = useLocale();
  const { user } = useAuth();
  const activePath = pathname === "/" ? "/" : navigation.find((item) => pathname === item.href || pathname.startsWith(`${item.href}/`))?.href ?? `/${pathname.split("/")[1]}`;

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="topbar-inner">
          <BrandMark />
          <nav className="desktop-nav" aria-label="Primary navigation">
            {navigation.slice(0, 6).map((item) => {
              const Icon = item.icon;
              const active = activePath === item.href;
              return (
                <Link key={item.href} href={item.href} className={`top-link ${active ? "is-active" : ""}`}>
                  <Icon size={16} strokeWidth={1.8} />
                  {t(item.labelKey)}
                </Link>
              );
            })}
          </nav>
          <div className="topbar-actions">
            <button type="button" className="icon-button search-button" aria-label="Search">
              <Search size={18} />
            </button>
            <Link href="/notifications" className="icon-button notification-button" aria-label={t("navigation.notifications")}>
              <Bell size={18} />
              <span className="notification-dot" />
            </Link>
            <label className="language-select">
              <span className="sr-only">Language</span>
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
      <nav className="mobile-bottom-nav" aria-label="Mobile navigation">
        {navigation.filter((item) => ["/", "/projects", "/assets", "/create/document", "/create/presentation", "/create/research", "/create/music", "/account"].includes(item.href)).map((item) => {
          const Icon = item.icon;
          const active = activePath === item.href;
          return (
            <Link key={item.href} href={item.href} className={`mobile-nav-link ${active ? "is-active" : ""}`}>
              <Icon size={19} strokeWidth={active ? 2.2 : 1.8} />
              <span>{t(item.labelKey)}</span>
            </Link>
          );
        })}
        <Link href="/projects?create=1" className="mobile-create-link" aria-label={t("navigation.create")}>
          <span><Plus size={20} /></span>
          <small>{t("navigation.create")}</small>
        </Link>
      </nav>
    </div>
  );
}
