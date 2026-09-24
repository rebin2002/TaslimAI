"use client";

import Link from "next/link";
import { useEffect } from "react";
import { ShieldAlert } from "lucide-react";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";
import { adminPageAccess } from "../lib/adminAccess";

export function AdminPage({ children }: Readonly<{ children: React.ReactNode }>) {
  const { user, loading } = useAuth();
  const pathname = usePathname();
  const router = useRouter();
  const access = adminPageAccess(loading, Boolean(user), Boolean(user?.isAdmin));

  useEffect(() => {
    if (access === "unauthenticated") router.replace(`/login?next=${encodeURIComponent(pathname)}`);
  }, [access, pathname, router]);

  if (access === "loading" || access === "unauthenticated") {
    return <div className="loading-state"><span className="loading-spinner" /></div>;
  }

  if (access === "forbidden") {
    return <section className="account-page admin-access-denied" aria-labelledby="admin-access-title">
      <div className="account-card">
        <ShieldAlert size={24} aria-hidden="true" />
        <p className="section-eyebrow">Restricted area</p>
        <h1 id="admin-access-title">Administrator access required</h1>
        <p>This internal operations area is available only to active Taslim administrators. No operations data has been loaded.</p>
        <Link className="primary-button" href="/account">Return to account</Link>
      </div>
    </section>;
  }

  return <>{children}</>;
}
