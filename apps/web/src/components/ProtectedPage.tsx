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

export function protectedPageHasHydratedSession(user: unknown): boolean {
  // A session counts as hydrated only once the auth provider has resolved a
  // real user from the post-login cookie; a signed-out session stays
  // indistinguishable from "not yet hydrated" and never opens the content.
  // A malformed payload - anything the server could not have produced as a
  // resolved session, such as a truthy object without a usable identity -
  // must deny just like an absent one. The stable id check mirrors the
  // identity contract the server-side requireAuthenticatedPage probe already
  // enforces, so a misrouted or malformed upstream response cannot open the
  // client gate either.
  if (typeof user !== "object" || user === null) return false;
  const id = (user as { id?: unknown }).id;
  return typeof id === "string" && id.trim().length > 0;
}

export function shouldArmBootstrapWatchdog(loading: boolean, hasHydratedSession: boolean): boolean {
  // The watchdog recovers a protected page from a session read that never
  // settles while nobody is signed in. It must never run for a hydrated
  // session: the provider's bootstrap flag can stay unresolved when a
  // completed sign-in supersedes the session read issued at mount, and
  // treating that stale flag as "no session" would evict the signed-in user
  // - including a first-login account still inside the onboarding flow -
  // back to /login despite a valid, persistent session.
  return loading && !hasHydratedSession;
}

export function ProtectedPage({ children }: Readonly<{ children: React.ReactNode }>) {
  const { user, loading } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const hasHydratedSession = protectedPageHasHydratedSession(user);

  useEffect(() => {
    // A settled absence *or a malformed payload* is a settled absence of a
    // session: both return the visitor to /login rather than holding the
    // protected shell open.
    if (!loading && !hasHydratedSession) router.replace(protectedPageLoginPath(pathname, window.location.search));
  }, [hasHydratedSession, loading, pathname, router]);

  useEffect(() => {
    if (!shouldArmBootstrapWatchdog(loading, hasHydratedSession)) return;
    // A stalled session bootstrap must not leave a protected page in an
    // indefinite loading state. The server-side Health gate is authoritative;
    // this is a client-side recovery path for network and proxy failures, and
    // it is cleared as soon as any session hydrates.
    const search = window.location.search;
    const timeout = window.setTimeout(() => router.replace(protectedPageLoginPath(pathname, search)), PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    return () => window.clearTimeout(timeout);
  }, [hasHydratedSession, loading, pathname, router]);

  // The hydrated session wins over the bootstrap flag so protected content,
  // including the first-run onboarding flow, stays mounted after login even
  // when a superseded session read left `loading` unresolved. The gate opens
  // only on the hardened predicate, never on a truthy-but-malformed session.
  if (!hasHydratedSession) return <div className="loading-state" role="status" aria-live="polite" aria-busy="true"><span className="loading-spinner" aria-hidden="true" /></div>;
  return <>{children}</>;
}
