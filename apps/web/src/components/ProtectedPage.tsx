"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";

export const PROTECTED_PAGE_AUTH_TIMEOUT_MS = 10_000;

export function protectedPageLoginPath(pathname: string, search = ""): string {
  // Protected views hydrate their working state from the query string, so an
  // expired session must return the user to the full location rather than only
  // the pathname that usePathname reports.
  const query = search.startsWith("?") ? search.slice(1) : search;
  const target = query.length > 0 ? `${pathname}?${query}` : pathname;
  // The query is part of the destination, so it cannot widen the validation
  // that already applies to the path: anything unsafe falls back to the root.
  const safePath = target.startsWith("/") && !target.startsWith("//") && !/[\u0000-\u001f\u007f]/.test(target) ? target : "/";
  return `/login?next=${encodeURIComponent(safePath)}`;
}

export function ProtectedPage({ children }: Readonly<{ children: React.ReactNode }>) {
  const { user, loading } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (!loading && !user) router.replace(protectedPageLoginPath(pathname, window.location.search));
  }, [loading, pathname, router, user]);

  useEffect(() => {
    if (!loading) return;
    // A stalled session bootstrap must not leave a protected page in an
    // indefinite loading state. The server-side Health gate is authoritative;
    // this is a client-side recovery path for network and proxy failures.
    const search = window.location.search;
    const timeout = window.setTimeout(() => router.replace(protectedPageLoginPath(pathname, search)), PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    return () => window.clearTimeout(timeout);
  }, [loading, pathname, router]);

  if (loading || !user) return <div className="loading-state" role="status" aria-busy="true"><span className="loading-spinner" /></div>;
  return <>{children}</>;
}
