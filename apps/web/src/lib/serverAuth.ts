import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const configuredApiUrl = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
const SESSION_CHECK_TIMEOUT_MS = 5_000;

function safeReturnPath(path: string): string {
  return path.startsWith("/") && !path.startsWith("//") && !/[\u0000-\u001f\u007f]/.test(path) ? path : "/";
}

function loginRedirect(path: string): never {
  redirect(`/login?next=${encodeURIComponent(safeReturnPath(path))}`);
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
    return response.ok;
  } catch {
    // Keep upstream availability, timeout, and response details out of the rendered page.
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
