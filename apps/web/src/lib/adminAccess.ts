export type AdminPageAccess = "loading" | "unauthenticated" | "forbidden" | "allowed";

export function adminPageAccess(loading: boolean, authenticated: boolean, isAdmin: boolean): AdminPageAccess {
  if (loading) return "loading";
  if (!authenticated) return "unauthenticated";
  return isAdmin ? "allowed" : "forbidden";
}
