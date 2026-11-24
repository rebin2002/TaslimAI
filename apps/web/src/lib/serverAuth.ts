import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const configuredApiUrl = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
const SESSION_CHECK_TIMEOUT_MS = 5_000;
// A return path travels in a Location header and then in a `next` query
// parameter, so it is capped well below any header-size limit. Overflow drops
// the query and keeps the pathname, which is the safe half of the destination.
const MAX_RETURN_PATH_LENGTH = 2_048;
type SessionProbePayload = {
  user?: { id?: unknown; personalWorkspaceId?: unknown };
  personalWorkspace?: { id?: unknown; type?: unknown; role?: unknown };
};

/** Shape of Next.js page `searchParams`, after the framework has decoded them. */
export type PageSearchParams = Record<string, string | string[] | undefined> | URLSearchParams;

function isInternalPath(value: string): boolean {
  // Same rules the sign-in form already applies to `next` (see
  // apps/web/src/lib/authRedirect.ts): an absolute path on this origin only.
  // `//host` is protocol-relative, `/\host` is treated as one by some clients,
  // and control characters could split the Location header.
  return value.startsWith("/")
    && !value.startsWith("//")
    && !value.includes("\\")
    && !/[\u0000-\u001f\u007f]/.test(value);
}

function safeReturnPath(path: string): string {
  return isInternalPath(path) ? path : "/";
}

function collectQueryValues(searchParams: PageSearchParams): [string, string][] {
  const pairs: [string, string][] = [];
  if (searchParams instanceof URLSearchParams) {
    for (const [key, value] of searchParams) pairs.push([key, value]);
    return pairs;
  }
  for (const [key, value] of Object.entries(searchParams)) {
    if (value === undefined || key.length === 0) continue;
    // Next.js reports a repeated parameter as an array; every occurrence is
    // part of the requested state, so all of them are preserved in order.
    for (const item of Array.isArray(value) ? value : [value]) pairs.push([key, item]);
  }
  return pairs;
}

/**
 * Rebuild the requested location from a pathname plus the page's decoded query
 * parameters, so a server-side gate can promise back the same deep link the
 * client-side boundary (`protectedPageLoginPath`) already preserves.
 *
 * Values are re-encoded with `encodeURIComponent`, matching how the app builds
 * these links in the first place (see `assetDestination` in
 * apps/web/src/lib/searchNavigation.ts): separators, `&`, `=`, `/`, spaces and
 * already-malformed sequences such as `%zz` stay inside their own value instead
 * of becoming structure. The result is then held to the internal-path rules, so
 * nothing here can widen the destination beyond this origin.
 */
export function internalReturnPath(pathname: string, searchParams?: PageSearchParams): string {
  const base = isInternalPath(pathname) ? pathname : "/";
  if (!searchParams) return base;
  const query = collectQueryValues(searchParams)
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
    .join("&");
  if (query.length === 0) return base;
  const target = `${base}?${query}`;
  return isInternalPath(target) && target.length <= MAX_RETURN_PATH_LENGTH ? target : base;
}

function loginRedirect(path: string): never {
  redirect(`/login?next=${encodeURIComponent(safeReturnPath(path))}`);
}

function hasSessionIdentity(payload: unknown): payload is SessionProbePayload {
  if (!payload || typeof payload !== "object") return false;
  const session = payload as SessionProbePayload;
  const userId = session.user?.id;
  const userWorkspaceId = session.user?.personalWorkspaceId;
  const workspaceId = session.personalWorkspace?.id;
  if (![userId, userWorkspaceId, workspaceId].every(value => typeof value === "string" && value.trim().length > 0)) return false;
  return userWorkspaceId === workspaceId
    && session.personalWorkspace?.type === "Personal"
    && session.personalWorkspace?.role === "Owner";
}

function isJsonResponse(response: Response): boolean {
  return response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() === "application/json";
}

async function hasValidSession(authCookie: string): Promise<boolean> {
  // Cookie values come from the browser. Reject delimiters/control characters
  // before interpolating the value into the upstream Cookie header.
  if (!authCookie || /[\u0000-\u001f\u007f\s;,]/.test(authCookie)) return false;

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), SESSION_CHECK_TIMEOUT_MS);
  try {
    const response = await fetch(`${configuredApiUrl}/api/auth/me`, {
      headers: { cookie: `taslim.auth=${authCookie}` },
      cache: "no-store",
      signal: controller.signal,
    });
    if (!response.ok) return false;
    if (!isJsonResponse(response)) return false;
    // A successful proxy/fallback response is not proof of authentication.
    // Require the stable /api/auth/me contract so a misrouted or malformed
    // upstream cannot fail open at this protected-page boundary.
    return hasSessionIdentity(await response.json());
  } catch {
    // Keep upstream availability, timeout, parse, and response details out of the rendered page.
    return false;
  } finally {
    clearTimeout(timeout);
  }
}

/**
 * Server-side defense in depth for pages whose content must not render before
 * the browser-side auth provider has hydrated. The API remains authoritative
 * for the session and active-user check.
 */
export async function requireAuthenticatedPage(path: string): Promise<void> {
  let authCookie: string | undefined;
  try {
    authCookie = (await cookies()).get("taslim.auth")?.value;
  } catch {
    // A cookie-store failure must not turn into a framework error page.
    loginRedirect(path);
  }
  if (!authCookie || !(await hasValidSession(authCookie))) loginRedirect(path);
}
