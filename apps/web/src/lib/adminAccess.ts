export type AdminPageAccess = "loading" | "unauthenticated" | "forbidden" | "allowed";

export const ADMIN_PAGE_AUTH_TIMEOUT_MS = 10_000;

export function adminPageAccess(loading: boolean, authenticated: boolean, isAdmin: boolean, authTimedOut = false): AdminPageAccess {
  if (authTimedOut) return "unauthenticated";
  if (loading) return "loading";
  if (!authenticated) return "unauthenticated";
  return isAdmin ? "allowed" : "forbidden";
}
