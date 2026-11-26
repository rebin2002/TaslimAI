"use client";

import { useEffect, useState } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";
import { protectedPageBootstrapStatusText, readBrowserProtectedPageBootstrapLocale, type ProtectedPageBootstrapLocale } from "../lib/protectedPageI18n";

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

  // The announcement starts on English so the first client render matches the
  // server-rendered shell, then takes the visitor's own language once the
  // document exists - the same post-hydration restore LocaleProvider uses for
  // the document's lang/dir. A boundary that only ever announced in English
  // would be as much of a failure for an Arabic or Kurdish reader as the
  // unnamed region it replaces.
  const [bootstrapLocale, setBootstrapLocale] = useState<ProtectedPageBootstrapLocale>("en");
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- restore the visible language after hydration so the live region is not English-only.
    setBootstrapLocale(readBrowserProtectedPageBootstrapLocale());
  }, []);

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
    // The query is read when the timer fires, not when the watchdog armed: a
    // protected view hydrates its state from the query and rewrites it while
    // the bootstrap window is open, so an arm-time snapshot evicts the user to
    // a deep link they were no longer on. The settled-absence path above
    // already reads it at fire time, and both eviction paths must agree on the
    // same location. `search` stays out of the dependency list on purpose - the
    // timer must not be re-armed by a query rewrite it is meant to observe.
    const timeout = window.setTimeout(() => router.replace(protectedPageLoginPath(pathname, window.location.search)), PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    return () => window.clearTimeout(timeout);
  }, [hasHydratedSession, loading, pathname, router]);

  // The hydrated session wins over the bootstrap flag so protected content,
  // including the first-run onboarding flow, stays mounted after login even
  // when a superseded session read left `loading` unresolved. The gate opens
  // only on the hardened predicate, never on a truthy-but-malformed session.
  // The region carries an authored name in the visitor's language. Its only
  // descendant is the decorative `aria-hidden` spinner, so without a label the
  // accessible-name computation resolves to the empty string and a screen
  // reader on a stalled or expiring session hears silence followed by an
  // unannounced route change; the sibling AdminPage boundary names the
  // identical class the same way.
  if (!hasHydratedSession) return <div className="loading-state" role="status" aria-live="polite" aria-busy="true" aria-label={protectedPageBootstrapStatusText(bootstrapLocale)}><span className="loading-spinner" aria-hidden="true" /></div>;
  return <>{children}</>;
}
