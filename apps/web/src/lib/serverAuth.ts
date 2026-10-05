import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const configuredApiUrl = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");

function loginRedirect(path: string): never {
  redirect(`/login?next=${encodeURIComponent(path)}`);
}

async function hasValidSession(authCookie: string): Promise<boolean> {
  try {
    const response = await fetch(`${configuredApiUrl}/api/auth/me`, {
      headers: { cookie: `taslim.auth=${authCookie}` },
      cache: "no-store",
    });
    return response.ok;
  } catch {
    // Keep upstream availability and response details out of the rendered page.
    return false;
  }
}

/**
 * Server-side defense in depth for pages whose content must not render before
 * the browser-side auth provider has hydrated. The API remains authoritative
 * for the session and active-user check.
 */
export async function requireAuthenticatedPage(path: string): Promise<void> {
  const authCookie = (await cookies()).get("taslim.auth")?.value;
  if (!authCookie || !(await hasValidSession(authCookie))) loginRedirect(path);
}
