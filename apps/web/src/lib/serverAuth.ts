import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const configuredApiUrl = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
const SESSION_CHECK_TIMEOUT_MS = 5_000;
type SessionProbePayload = {
  user?: { id?: unknown; personalWorkspaceId?: unknown };
  personalWorkspace?: { id?: unknown; type?: unknown; role?: unknown };
};

function safeReturnPath(path: string): string {
  return path.startsWith("/") && !path.startsWith("//") && !/[\u0000-\u001f\u007f]/.test(path) ? path : "/";
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
