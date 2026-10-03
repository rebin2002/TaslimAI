"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useLocale } from "@/components/LocaleProvider";
import { matchesNavigationPath, primaryNavigation } from "@/lib/navigation";

export function MobileNav({ unreadCount }: Readonly<{ unreadCount: number }>) {
  const pathname = usePathname();
  const { t } = useLocale();
  return (
    <nav className="mobile-bottom-nav" aria-label={t("navigation.mobilePrimary")}>
      {primaryNavigation.map((item) => {
        const Icon = item.icon;
        const active = matchesNavigationPath(pathname, item.href) || (item.href === "/create" && pathname.startsWith("/create"));
        const isNotification = item.href === "/notifications";
        const accessibleLabel = isNotification && unreadCount > 0 ? `${t(item.labelKey)}, ${t("notification.unread", { count: String(unreadCount) })}` : undefined;
        return (
          <Link key={item.href} href={item.href} className={`mobile-nav-link ${item.href === "/create" ? "mobile-nav-create" : ""} ${active ? "is-active" : ""}`} aria-current={active ? "page" : undefined} aria-label={accessibleLabel}>
            <span className="mobile-nav-icon">
              <Icon size={item.href === "/create" ? 20 : 19} strokeWidth={active ? 2.2 : 1.8} aria-hidden="true" />
              {isNotification && unreadCount > 0 && <span className="mobile-activity-badge" aria-hidden="true">{unreadCount > 99 ? "99+" : unreadCount}</span>}
            </span>
            <span>{t(item.labelKey)}</span>
          </Link>
        );
      })}
    </nav>
  );
}
