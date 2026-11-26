/**
 * Issue #697 follow-up — form-encoded malformed/unrecognized Server Action
 * POST regression harness. Test-only and loopback-only: the Playwright dev
 * server is bound to 127.0.0.1, and E2E_WEB_URL is accepted only for an
 * explicit loopback host. No production/external probe, credential, provider
 * call or private data; every payload is a fixed anonymous constant.
 *
 * Why this file exists. PR #764 (merged 2026-11-16,
 * `server-action-malformed-post.spec.ts`) pinned the text/plain (+RSC)
 * malformed-action handler contract: those requests are rejected before React
 * renders (400 "Invalid Server Action request."). The 2026-11-18 triage lane
 * on #697 identified the uncovered shape that matches the production events
 * (2026-10-30 / 11-03 / 11-09 / 11-18): `application/x-www-form-urlencoded`
 * with a malformed `Next-Action` id (the literal prod value was `"x"`) takes
 * Next 16.4.3's legacy server-action fallback and renders the root
 * not-found boundary OUTSIDE `layout.tsx`'s LocaleProvider tree. On unpatched
 * main (`apps/web/src/app/not-found.tsx` calls `useLocale()`
 * unconditionally) this throws `useLocale must be used within LocaleProvider`
 * during SSR -> HTTP 500. The source fix is owned by open PR #613
 * (`not-found.tsx`, `not-found.test.tsx`, `LocaleProvider.tsx`); this lane
 * touches NO source files.
 *
 * Dual contract, deliberately robust across the #613 merge so the file never
 * needs a flag flip:
 *  - If a shape is answered 5xx (known defect present): the response must be
 *    CONTAINED — no secret/env markers, no stack — and the route must be
 *    immediately healthy again (GET / -> 200, page navigable). Provider- and
 *    server-reference-message containment is additionally asserted when an
 *    externally started loopback server is selected with E2E_WEB_URL, matching
 *    the deployed response shape; the Playwright-managed dev server may inject
 *    its own local-only error overlay and is excluded from that check only.
 *  - If a shape is answered 400/404 (fixed or framework-rejected): the safe
 *    response shell must never contain the locale-error or server-reference
 *    text (positive generic-copy rendering is a component-level assertion in
 *    the existing unit suite); the route stays navigable.
 *  - Controls: text/plain malformed `x` keeps the #764 400 contract (no
 *    cross-shape interference); an ordinary unmatched-path GET still renders
 *    the provider-backed not-found page as 404 (locale-boundary control).
 */
import { test, expect, type APIRequestContext } from "@playwright/test";

const DEFAULT_WEB_URL = "http://localhost:3000";
const E2E_WEB_URL = process.env.E2E_WEB_URL;
const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]"]);
const LOOPBACK_WEB_URL_ERROR =
  "Unsafe E2E_WEB_URL: expected an absolute http(s) URL on localhost, 127.0.0.1, or [::1]. Refusing to launch tests or send requests.";

function assertLoopbackWebURL(value: string): void {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error(LOOPBACK_WEB_URL_ERROR);
  }

  if (
    (parsed.protocol !== "http:" && parsed.protocol !== "https:") ||
    !LOOPBACK_HOSTS.has(parsed.hostname.toLowerCase()) ||
    parsed.username !== "" ||
    parsed.password !== ""
  ) {
    throw new Error(LOOPBACK_WEB_URL_ERROR);
  }
}

// Playwright imports this spec during test discovery, before creating browser
// or request fixtures. Fail closed here so a hostile E2E_WEB_URL cannot be
// contacted by any test in this file.
assertLoopbackWebURL(E2E_WEB_URL ?? DEFAULT_WEB_URL);

/** An externally started loopback test server when E2E_WEB_URL is set. */
const EXTERNAL_LOOPBACK_HARNESS = Boolean(E2E_WEB_URL);

const FORM_ACTION_IDS: ReadonlyArray<readonly [string, string]> = [
  // Exact malformed id observed in production server-reference errors.
  ['literal "x" (prod shape)', "x"],
  ["unrecognized 40-hex id", "0000000000000000000000000000000000000000"],
];

const FORM_BODY = "email=anon%40example.test&note=synthetic";

// Fixed anonymous constants only — none of these may appear in any response.
const SECRET_MARKERS: ReadonlyArray<string> = [
  "process.env",
  "NEXT_PUBLIC_",
  "API_KEY",
  "api_key",
  "-----BEGIN",
];

const INVALID_ACTION_BODY = "Invalid Server Action request.";
const LOCALE_ERROR = "useLocale must be used within LocaleProvider";
const SERVER_REFERENCE_ERROR = "Server Reference ID did not match";

async function postFormMalformedAction(
  request: APIRequestContext,
  baseURL: string,
  path: string,
  actionId: string,
  withOrigin: boolean,
) {
  assertLoopbackWebURL(baseURL);
  return request.post(path, {
    maxRedirects: 0,
    headers: {
      "next-action": actionId,
      "content-type": "application/x-www-form-urlencoded",
      ...(withOrigin ? { origin: new URL(baseURL).origin } : {}),
    },
    data: FORM_BODY,
  });
}

/**
 * Branch-independent containment invariants: no secret/env markers anywhere
 * in the body, no open-redirect Location, and the route answers a healthy
 * 200 immediately after (one request cannot take the route down).
 */
async function assertContained(request: APIRequestContext, res: Awaited<ReturnType<typeof request.post>>, body: string) {
  const status = res.status();
  if (status >= 300 && status < 400) {
    const location = res.headers()["location"];
    expect(location, "3xx must expose a Location").toBeTruthy();
    expect(
      new URL(location as string, res.url()).origin,
      "redirect must stay same-origin loopback",
    ).toBe(new URL(res.url()).origin);
  }
  for (const marker of SECRET_MARKERS) {
    expect(body, `response body must not contain ${marker}`).not.toContain(marker);
  }
  const healthy = await request.get("/");
  expect(healthy.status(), "route must recover to 200 immediately").toBe(200);
}

test("loopback URL guard allows local origins and rejects external targets before POST", async () => {
  for (const url of [
    "http://127.0.0.1:3000",
    "https://localhost:4443",
    "http://[::1]:3000",
  ]) {
    expect(() => assertLoopbackWebURL(url)).not.toThrow();
  }

  const attemptedRequests: string[] = [];
  const request = {
    post: async (path: string) => {
      attemptedRequests.push(path);
      throw new Error("request must not be sent");
    },
  } as unknown as APIRequestContext;

  for (const url of [
    "https://example.com",
    "http://localhost.attacker.example:3000",
    "http://127.0.0.2:3000",
  ]) {
    await expect(
      postFormMalformedAction(request, url, "/", "x", true),
    ).rejects.toThrow(/Unsafe E2E_WEB_URL/);
  }
  expect(attemptedRequests).toEqual([]);
});

test.describe("form-encoded malformed Server Action POST -> not-found locale boundary (issue #697 follow-up)", () => {
  for (const [name, actionId] of FORM_ACTION_IDS) {
    for (const withOrigin of [true, false]) {
      test(`POST / form-encoded malformed next-action (${name}, origin ${withOrigin ? "sent" : "omitted"}) is never an uncontained failure`, async ({ request, baseURL }) => {
        const res = await postFormMalformedAction(request, baseURL as string, "/", actionId, withOrigin);
        const status = res.status();
        const body = await res.text();
        // Either fixed (400/404) or the tracked #613 defect (500) — nothing
        // else (no 502/503/504, no open redirects, no crash class).
        expect([400, 404, 500], `unexpected status ${status}`).toContain(status);
        await assertContained(request, res, body);
        if (status === 500) {
          // Known-defect branch (unpatched main): #613 owns the source fix.
          // Production-shape containment: the SSR locale throw and the
          // server-reference log line must not reach the client.
          if (EXTERNAL_LOOPBACK_HARNESS) {
            expect(body).not.toContain(LOCALE_ERROR);
            expect(body).not.toContain(SERVER_REFERENCE_ERROR);
          }
        } else if (status === 404) {
          // Fixed branch (#613 merged): the shell must never carry the
          // provider error text. Positive copy rendering is asserted at
          // component level in src/app/not-found.test.tsx; the raw streamed
          // 404 shell only carries the flight payload, so this lane asserts
          // containment only.
          expect(body).not.toContain(LOCALE_ERROR);
          expect(body).not.toContain(SERVER_REFERENCE_ERROR);
        } else {
          expect(body).toBe(INVALID_ACTION_BODY);
        }
      });
    }
  }

  test("POST /nonexistent-route-697 with the same malformed form shape stays contained", async ({ request, baseURL }) => {
    const res = await postFormMalformedAction(request, baseURL as string, "/nonexistent-route-697", "x", true);
    const status = res.status();
    const body = await res.text();
    expect([400, 404, 500], `unexpected status ${status}`).toContain(status);
    await assertContained(request, res, body);
    if (status !== 400 && EXTERNAL_LOOPBACK_HARNESS) {
      expect(body).not.toContain(LOCALE_ERROR);
    }
  });

  test('control: text/plain malformed next-action "x" keeps the merged #764 400 contract', async ({ request }) => {
    const res = await request.post("/", {
      maxRedirects: 0,
      headers: {
        "next-action": "x",
        "content-type": "text/plain;charset=UTF-8",
      },
      data: "[]",
    });
    expect(res.status()).toBe(400);
    expect(await res.text()).toBe(INVALID_ACTION_BODY);
  });

  test("control: ordinary unmatched-path GET still renders the provider-backed 404 (no 5xx, no locale throw)", async ({ request }) => {
    const res = await request.get("/nonexistent-route-697");
    expect(res.status()).toBe(404);
    const body = await res.text();
    expect(body).not.toContain(LOCALE_ERROR);
    expect(body).not.toContain(SERVER_REFERENCE_ERROR);
  });

  test("browser: a same-origin form-encoded malformed POST cannot brick the session — route stays navigable afterwards", async ({ page, baseURL, request }) => {
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(String(error?.message ?? error)));
    await page.goto("/");
    const status = await page.evaluate(async () => {
      const res = await fetch("/", {
        method: "POST",
        headers: {
          "next-action": "x",
          "content-type": "application/x-www-form-urlencoded",
        },
        body: "email=anon%40example.test",
      });
      return res.status;
    });
    expect([400, 404, 500], `unexpected in-page status ${status}`).toContain(status);
    // The secondary production signal must never surface as a page error
    // from this path in either branch of the contract.
    expect(pageErrors.join("\n")).not.toContain(LOCALE_ERROR);
    // Recovery navigation: the app is still usable immediately after.
    await page.goto(new URL("/register", baseURL as string).toString());
    await expect(page.locator("body")).toBeVisible();
    expect(page.url()).toContain("/register");
    const healthy = await request.get("/");
    expect(healthy.status()).toBe(200);
  });
});
