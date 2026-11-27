import { test, expect, apiJson, E2E_API_URL } from "./fixtures";

// Browser lane of the CSRF rejection contract (PR #1039).
//
// The API lane (apps/api.Tests/CsrfRejectionEnvelopeTests.cs) already proves the
// envelope with HttpClient against the in-memory TestServer, including the
// /api/auth/register spellings. Nothing here repeats that claim at the same
// layer. What only a real browser can prove is that the rejection a user
// actually receives -- issued from the app origin with the app's own cookie jar,
// and again through the app's own same-origin API proxy -- is the
// machine-readable CSRF_VALIDATION_FAILED envelope the web client depends on to
// recognize a stale token, refresh once, and retry (apps/web/src/lib/api.ts keys
// that recovery solely on this code). A generic Problem Details body, which is
// what the MVC enforcement layer returned before this fix, is invisible to that
// client and turns a recoverable rejection into a form that cannot submit again.
//
// These tests also pin the other half of the contract: antiforgery strictness is
// unchanged. A refusal must never apply the mutation, and a valid double-submit
// pair must still be accepted.
//
// Request budget: the anonymous authentication endpoints share the fixed-window
// "authentication" limiter (60 requests/minute, partitioned by client IP, and
// counted before the antiforgery boundary). Every request in the describe block
// below that touches those endpoints is issued here, four in total, and the
// remaining coverage uses authenticated workspace routes, which are partitioned
// by user id and carry no authentication policy. That keeps this lane
// comfortably inside the suite's shared budget on CI.
//
// Provider-free by construction: every refusal happens at the antiforgery or
// authorization boundary, before any generation, storage, billing or payment
// code runs, and the only accepted write is a project row. No provider call, no
// secret, and no real user data: the accounts are throwaway fixture users. No
// sleeps, wall-clock budgets, or counters that depend on scheduling, so the
// assertions are deterministic.

const CSRF_CODE = "CSRF_VALIDATION_FAILED";
const CSRF_MESSAGE = "Request validation failed.";
// Never a real credential: it exists only to prove the rejection body does not
// echo the submitted payload back to the browser.
const PROBE_PASSWORD = "CsrfProbeOnly!123";
const PROBE_EMAIL = "csrf-browser-anonymous@example.test";

type BrowserPost = {
  status: number;
  contentType: string | null;
  text: string;
};

/**
 * POSTs from inside the loaded page so the request is made by a real browser:
 * its own cookie jar, a cross-origin preflight against the API CORS policy, and
 * the actual network stack. A root-relative URL is answered by the app's
 * same-origin API proxy instead of the API origin directly.
 */
async function postFromPage(
  page: import("@playwright/test").Page,
  url: string,
  payload: unknown,
  headers: Record<string, string> = {},
): Promise<BrowserPost> {
  return page.evaluate(
    async ({ target, body, extra }) => {
      const response = await fetch(target, {
        method: "POST",
        credentials: "include",
        headers: { "content-type": "application/json", ...extra },
        body,
      });
      return {
        status: response.status,
        contentType: response.headers.get("content-type"),
        text: await response.text(),
      };
    },
    { target: url, body: JSON.stringify(payload), extra: headers },
  );
}

/** Reads the double-submit pair the browser already holds, without a page reload. */
async function tokenFromBrowserContext(page: import("@playwright/test").Page): Promise<string> {
  const issued = await page.request.get(`${E2E_API_URL}/api/auth/csrf`);
  expect(issued.ok(), `CSRF token issuance failed: ${issued.status()}`).toBeTruthy();
  const { token } = await issued.json() as { token: string };
  expect(token).toBeTruthy();
  return token;
}

function expectStableCsrfEnvelope(probe: BrowserPost, label: string) {
  expect(probe.status, `${label}: status ${probe.status} body ${probe.text.slice(0, 200)}`).toBe(400);
  expect(probe.contentType ?? "", label).toContain("application/json");
  const body = JSON.parse(probe.text) as Record<string, unknown>;
  // Not a Problem Details document: those members are exactly what the web
  // client cannot use to recognize a stale token.
  for (const member of ["type", "title", "status", "detail", "instance", "errors", "traceId", "trace_id"]) {
    expect(body, `${label}: must not expose Problem Details member "${member}"`).not.toHaveProperty(member);
  }
  expect(body.error, `${label}: rejection carries an error member`).toBeTruthy();
  const error = body.error as Record<string, unknown>;
  expect(error.code, label).toBe(CSRF_CODE);
  expect(error.message, label).toBe(CSRF_MESSAGE);
  // The refusal text is fixed and safe: no token material, no submitted body.
  expect(probe.text, `${label}: must not leak the antiforgery cookie name`).not.toContain("taslim.csrf");
  expect(probe.text, `${label}: must not echo the submitted credential`).not.toContain(PROBE_PASSWORD);
  return body;
}

test.describe("CSRF rejection envelope from the browser", () => {
  // The canonical spelling is refused by AntiforgeryValidationMiddleware, which
  // names the anonymous authentication endpoints by exact path. These two
  // spellings still route to the same controller but miss that exact-path list,
  // so they are answered by the MVC antiforgery filter -- the layer this fix
  // normalizes. Asserted together so one request carries both claims.
  test("canonical, trailing-separator and case-variant spellings all return the same stable envelope", async ({ page }) => {
    await page.goto("/login");
    const payload = { email: PROBE_EMAIL, password: PROBE_PASSWORD };
    const canonical = await postFromPage(page, `${E2E_API_URL}/api/auth/login`, payload);
    const trailingSeparator = await postFromPage(page, `${E2E_API_URL}/api/auth/login/`, payload);
    const caseVariant = await postFromPage(page, `${E2E_API_URL}/API/AUTH/LOGIN`, payload);

    for (const [label, probe] of [
      ["middleware layer /api/auth/login", canonical],
      ["MVC layer /api/auth/login/", trailingSeparator],
      ["MVC layer /API/AUTH/LOGIN", caseVariant],
    ] as Array<[string, BrowserPost]>) {
      expectStableCsrfEnvelope(probe, label);
    }

    // The contract must not depend on which enforcement layer observed the
    // rejection first: the browser receives one byte-identical body.
    expect(trailingSeparator.text, "trailing-separator body differs from the middleware body").toBe(canonical.text);
    expect(caseVariant.text, "case-variant body differs from the middleware body").toBe(canonical.text);
  });

  test("the app's same-origin API proxy forwards the rejection with the same status and envelope", async ({ page }) => {
    await page.goto("/login");
    // Root-relative, so the browser talks to the app and the app's own proxy
    // (apps/web/src/app/api/[...path]/route.ts) forwards the API rejection.
    // This is the exact response the web client inspects to decide whether it
    // may refresh its token once and retry.
    expectStableCsrfEnvelope(
      await postFromPage(page, `/api/auth/login`, { email: PROBE_EMAIL, password: PROBE_PASSWORD }),
      "same-origin proxy",
    );
  });

  test("a tampered request token on an authenticated browser session is refused and the mutation never lands", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const token = await tokenFromBrowserContext(page);
    const projectName = "CSRF Tampered Browser Write";

    expectStableCsrfEnvelope(
      await postFromPage(
        page,
        `${E2E_API_URL}/api/workspaces/${auth.personalWorkspace.id}/projects`,
        { name: projectName, type: "Business", description: "Must never be created." },
        // A well-formed header that is not the value paired with the browser's
        // cookie. Strictness means this is refused, not waved through.
        { "X-CSRF-TOKEN": `${token}tampered` },
      ),
      "tampered double-submit pair",
    );

    const projects = await apiJson<Array<{ name: string }>>(
      page.request,
      "GET",
      `/api/workspaces/${auth.personalWorkspace.id}/projects`,
    );
    expect(projects.map((project) => project.name), "the refused mutation must not be persisted").not.toContain(projectName);
  });

  test("an anonymous write into another account's workspace is refused and never applied", async ({ authenticatedPage: page, request }) => {
    const owner = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const projectName = "CSRF Anonymous Cross-Account Write";
    // `request` is an isolated context: no session cookie, no double-submit pair.
    const anonymous = await request.post(
      `${E2E_API_URL}/api/workspaces/${owner.personalWorkspace.id}/projects`,
      { data: { name: projectName, type: "Business", description: "Must never be created." } },
    );
    // Authorization or antiforgery may answer first; either way it is a refusal,
    // never an accepted write and never an unenveloped server error.
    expect([400, 401], `anonymous write status ${anonymous.status()}`).toContain(anonymous.status());
    if (anonymous.status() === 400) {
      const body = await anonymous.json() as { error?: { code?: string } };
      expect(body.error?.code).toBe(CSRF_CODE);
    }

    const projects = await apiJson<Array<{ name: string }>>(
      page.request,
      "GET",
      `/api/workspaces/${owner.personalWorkspace.id}/projects`,
    );
    expect(projects.map((project) => project.name), "the anonymous write must not be persisted").not.toContain(projectName);
  });

  test("a valid double-submit pair issued by the browser still completes the mutation and reaches the UI", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const projectName = "CSRF Browser Accepted Pair";
    const created = await apiJson<{ id: string; name: string }>(
      page.request,
      "POST",
      `/api/workspaces/${auth.personalWorkspace.id}/projects`,
      { name: projectName, type: "Business", description: "Accepted double submit." },
    );
    expect(created.name).toBe(projectName);
    await page.goto("/projects");
    await expect(page.getByText(projectName)).toBeVisible();
  });
});
