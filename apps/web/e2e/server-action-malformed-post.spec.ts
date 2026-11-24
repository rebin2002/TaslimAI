/**
 * Issue #697 — disposable, loopback-only harness for malformed/unrecognized
 * Next.js Server Action POST behavior against the root route.
 *
 * Runs entirely against the Playwright-managed dev server bound to
 * 127.0.0.1 (see playwright.config.ts `webServer.command`). No production
 * probe, no credentials, no provider calls, no private data: every payload is
 * a fixed anonymous constant. The API origin env is deliberately unreachable
 * loopback in CI; none of these requests touch it.
 *
 * Recorded exact behavior on `main` (both `next dev` and `next start`, Next
 * 16.4.3) — see the PR evidence table. This spec pins the handler contract:
 *  - plain POST / with a malformed `next-action` header  -> 400 text
 *    "Invalid Server Action request." (never 500, never boundary HTML);
 *  - RSC-shaped POST / (`rsc: 1`) with an unknown id      -> 307 same-origin
 *    redirect to the RSC page fetch (never 500);
 *  - the "Server Reference ID did not match the expected format" message is
 *    server-log only and must never appear in a response body;
 *  - the locale-hook error class from error.tsx cannot be reached by these
 *    requests: the browser page stays navigable and emits no
 *    `useLocale must be used within LocaleProvider` page error;
 *  - one request cannot take the route down: a POST storm is followed by a
 *    healthy 200 navigation.
 */
import { test, expect, type APIRequestContext } from "@playwright/test";

const MALFORMED_ACTION_IDS: ReadonlyArray<readonly [string, string]> = [
  ["unrecognized 40-hex id", "0000000000000000000000000000000000000000"],
  ["non-hex junk", "zz-not-a-valid-id!!"],
  ["short junk", "abc"],
  ["empty id", ""],
];

const INVALID_ACTION_BODY = "Invalid Server Action request.";

async function postMalformedAction(
  request: APIRequestContext,
  actionId: string,
  headers: Record<string, string> = {},
) {
  return request.post("/", {
    maxRedirects: 0,
    headers: {
      "next-action": actionId,
      "content-type": "text/plain;charset=UTF-8",
      ...headers,
    },
    data: "[]",
  });
}

test.describe("malformed/unrecognized Server Action POST handling (issue #697)", () => {
  test("baseline anonymous GET / is a safe 200 without boundary or locale leakage", async ({ request }) => {
    const res = await request.get("/");
    expect(res.status()).toBe(200);
    expect(res.headers()["content-type"]).toContain("text/html");
    expect(res.headers()["x-content-type-options"]).toBe("nosniff");
    const body = await res.text();
    expect(body).not.toContain("useLocale must be used within LocaleProvider");
    expect(body).not.toContain("Server Reference ID");
    expect(body).not.toContain(INVALID_ACTION_BODY);
  });

  for (const [name, actionId] of MALFORMED_ACTION_IDS) {
    test(`POST / with malformed next-action (${name}) is answered 400, not 500, with fixed text`, async ({ request }) => {
      const res = await postMalformedAction(request, actionId);
      expect(res.status()).toBe(400);
      expect(res.headers()["content-type"]).toContain("text/plain");
      // Exact body: no HTML, no error boundary markup, no stack, no digest.
      expect(await res.text()).toBe(INVALID_ACTION_BODY);
    });
  }

  test("RSC-shaped malformed action POST is a same-origin 307 to the page RSC fetch, never 500", async ({ request }) => {
    const res = await postMalformedAction(request, "0000000000000000000000000000000000000000", { rsc: "1" });
    expect(res.status()).toBe(307);
    const location = res.headers()["location"];
    expect(location).toBeTruthy();
    const target = new URL(location as string, res.url());
    // Same-origin loopback redirect only — no open redirect, no data exfil path.
    expect(target.origin).toBe(new URL(res.url()).origin);
    expect(target.pathname).toBe("/");
    const followUp = await request.get(`${target.pathname}${target.search}`);
    expect(followUp.status()).toBe(200);
    expect(followUp.headers()["content-type"]).toContain("text/html");
  });

  test("a plain form POST without next-action still renders the page, not an error boundary", async ({ request }) => {
    const res = await request.post("/", {
      maxRedirects: 0,
      headers: { "content-type": "application/x-www-form-urlencoded" },
      data: "email=anon%40example.test",
    });
    expect(res.status()).toBe(200);
    const body = await res.text();
    expect(body).not.toContain("Something went wrong");
    expect(body).not.toContain("useLocale");
  });

  test("browser dispatch of the malformed POST keeps the app navigable with no locale-hook page error", async ({ page, baseURL }) => {
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(String(error?.message ?? error)));
    await page.goto("/");
    const status = await page.evaluate(async () => {
      const res = await fetch("/", {
        method: "POST",
        headers: {
          "next-action": "0000000000000000000000000000000000000000",
          "content-type": "text/plain;charset=UTF-8",
        },
        body: "[]",
      });
      return res.status;
    });
    // The in-page client sees the same handler contract: bad request, not a crash.
    expect(status).toBe(400);
    // The secondary production signal must not occur from this path:
    expect(pageErrors.join("\n")).not.toContain("useLocale must be used within LocaleProvider");
    // Route is still navigable afterwards (no crash, no redirect loop).
    await page.goto(new URL("/register", baseURL as string).toString());
    await expect(page.locator("body")).toBeVisible();
    expect(page.url()).toContain("/register");
  });

  test("a storm of malformed POSTs cannot take the route down", async ({ request }) => {
    const statuses = new Set<number>();
    for (let i = 0; i < 10; i += 1) {
      const res = await postMalformedAction(request, MALFORMED_ACTION_IDS[i % MALFORMED_ACTION_IDS.length][1]);
      statuses.add(res.status());
      expect(await res.text()).toBe(INVALID_ACTION_BODY);
    }
    expect([...statuses]).toEqual([400]);
    const healthy = await request.get("/");
    expect(healthy.status()).toBe(200);
  });
});
