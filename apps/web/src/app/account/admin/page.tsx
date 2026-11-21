"use client";

import Link from "next/link";
import { Activity, ArrowLeft, BarChart3 } from "lucide-react";
import { AdminPage } from "@/components/AdminPage";
import { useLocale } from "@/components/LocaleProvider";

const tools = [
  { href: "/account/admin/usage", title: "admin.centerUsage", description: "admin.centerUsageDescription", icon: BarChart3 },
  { href: "/account/admin/operations", title: "admin.centerOperations", description: "admin.centerOperationsDescription", icon: Activity },
] as const;

export default function AdminCenterPage() {
  const { t } = useLocale();

  return (
    <AdminPage>
      <section className="account-page" aria-labelledby="admin-center-title">
        <div className="account-header">
          <div>
            <Link className="back-link" href="/account">
              <ArrowLeft size={14} aria-hidden="true" /> {t("admin.backAccount")}
            </Link>
            <h1 id="admin-center-title">{t("admin.center")}</h1>
          </div>
        </div>
        <nav className="account-resource-grid" aria-labelledby="admin-center-title">
          {tools.map(({ href, title, description, icon: Icon }) => (
            <div className="account-card usage-link-card" key={href}>
              <p>{t(description)}</p>
              <Link className="secondary-button" href={href}>
                <Icon size={16} aria-hidden="true" /> {t(title)}
              </Link>
            </div>
          ))}
        </nav>
      </section>
    </AdminPage>
  );
}
