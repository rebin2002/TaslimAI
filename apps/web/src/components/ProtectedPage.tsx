"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";

export const PROTECTED_PAGE_AUTH_TIMEOUT_MS = 10_000;

export function protectedPageLoginPath(pathname: string): string {
  const safePath = pathname.startsWith("/") && !pathname.startsWith("//") && !/[\u0000-\u001f\u007f]/.test(pathname) ? pathname : "/";
  return `/login?next=${encodeURIComponent(safePath)}`;
}

export function ProtectedPage({ children }: Readonly<{ children: React.ReactNode }>) {
  const { user, loading } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (!loading && !user) router.replace(protectedPageLoginPath(pathname));
  }, [loading, pathname, router, user]);

  useEffect(() => {
    if (!loading) return;
    // A stalled session bootstrap must not leave a protected page in an
    // indefinite loading state. The server-side Health gate is authoritative;
    // this is a client-side recovery path for network and proxy failures.
    const timeout = window.setTimeout(() => router.replace(protectedPageLoginPath(pathname)), PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    return () => window.clearTimeout(timeout);
  }, [loading, pathname, router]);

  if (loading || !user) return <div className="loading-state" role="status" aria-busy="true"><span className="loading-spinner" /></div>;
  return <>{children}</>;
}
