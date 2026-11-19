import { test, expect } from "@playwright/test";
/**
 * Issue #714 / PR #1070 follow-up lane (test-only, disjoint file).
 *
 * The release-gate review on #1070 at head
 * `1ce8011ebffb447e97860d80fe4a4d084ae4809c` recorded one remaining
 * authenticated pre-consent transport gap:
 *
 *   Lane 4 of `health-preauth-data-boundary.acceptance.spec.ts` asserts only
 *   `gate.dataPlaneRequests()` for the authenticated navigation. That filter is
 *   driven by `dataPlanePath`, an *enumerated prefix* regex
 *   (`/api/health|documents|assets|uploads|files|records|reports|labs|
 *   medications|prescriptions|patients`). Its allow-list companion
 *   `unexpectedApiRequests()` is asserted for the ANONYMOUS navigation only.
 *   A Health Care component that hydrates a patient record through a generic,
 *   non-enumerated endpoint — `/api/search?kind=health`,
 *   `/api/generation/jobs/{id}`, `/api/graphql` — therefore leaves lane 4 green
 *   while the "no unauthorized health document exposure" invariant is violated.
 *
 * This lane closes exactly that gap and nothing else. It does NOT edit the file
 * owned by #1070 and it does NOT touch production behaviour.
 *
 * The fix is a direction reversal on the authenticated surface: instead of
 * asking "is this call on a list of things we know are bad?", the authenticated
 * pre-consent gate asks "is this call on the list of app-shell traffic we have
 * actually seen and account for?". Every `/api/*` request whose *pathname* is
 * not explicitly allow-listed is a violation, so an unknown generic endpoint
 * fails closed whether or not anyone thought to enumerate it.
 *
 *  1. the allow-list is pinned as data, derived from the real shell bootstrap
 *     (AuthProvider session, CSRF bootstrap, workspace resolution, the passive
 *     unread-count polls) and matched on pathname only, so a query string can
 *     never smuggle a record fetch past it;
 *  2. classifier specificity is proven both ways on synthetic URLs, including a
 *     direct cross-check against #1070's enumerated `dataPlanePath` regex that
 *     shows this gate catches requests that gate does not;
 *  3. a pristine synthetic authenticated navigation stays green, so the failure
 *     below cannot be unconditional;
 *  4. a negative control injects one generic non-enumerated record-bearing call
 *     (`GET /api/search?kind=health`) into that same navigation and the gate
 *     must go RED naming it.
 *
 * Everything here is synthetic and provider-free. Lanes 3 and 4 mount a fake
 * app shell and a fake data plane through `page.route`, so no backend, no
 * login, no provider call and no deployed change is involved, and no real
 * patient data exists anywhere in this file: the only payload is a canary
 * record that lives inside the fulfilment handler. Lanes 1 and 2 are pure
 * functions of their input and therefore deterministic by construction.
 */

/** The route under test: the Health Care surface an authenticated user sees before consent. */
const HEALTH_ROUTE = "/create/health";

/**
 * Known, accounted-for authenticated app-shell traffic on `/create/health`.
 *
 * Each entry is a *pathname*, matched exactly, never a prefix and never a
 * pattern that a caller-supplied segment could satisfy:
 *  - `/api/auth/me`                   `AuthProvider` session bootstrap (`api.me`).
 *  - `/api/auth/csrf`                 CSRF bootstrap (`lib/api.ts`).
 *  - `/api/auth/password-policy`      passive policy fetch (`api.passwordPolicy`).
 *  - `/api/workspaces`                active-workspace resolution (`api.listWorkspaces`).
 *  - `/api/notifications/unread-count` the shell bell's passive poll.
 *  - `/api/activity/unread-count`      the shell activity poll.
 *
 * Deliberately absent: `/api/workspaces/{id}/...`, `/api/notifications` (the
 * list body), `/api/projects`, `/api/search`, `/api/generation/*`, `/api/graphql`
 * and every data-plane prefix. Those are either record-bearing or not part of
 * the pre-consent shell bootstrap, so on this surface they are new API traffic
 * and must be justified by changing this list in review, not by shipping.
 */
const AUTHENTICATED_SHELL_ALLOW_LIST = [
  "/api/auth/me",
  "/api/auth/csrf",
  "/api/auth/password-policy",
  "/api/workspaces",
  "/api/notifications/unread-count",
  "/api/activity/unread-count",
] as const;

/** `/api/auth/*` is the one family allowed to carry path segments (session bootstrap). */
const AUTHENTICATED_SHELL_SEGMENT_PREFIXES = ["/api/auth/"] as const;

/**
 * #1070's enumerated data-plane regex, copied verbatim into this file for
 * cross-checking only. It is not used to decide anything here; the gate in this
 * lane is deny-by-default. Keeping the copy is what lets lane 2 demonstrate the
 * difference between the two designs on the same input instead of asserting it
 * in prose.
 */
const ENUMERATED_DATA_PLANE_PATH =
  /\/api\/(?:health|documents?|assets|uploads?|files|records|reports|labs?|medications|prescriptions|patients)(?:\/|$|\?|#)/i;

type ShellRequestKind = "non-api" | "allowed-shell" | "outside-allow-list";

type ShellRequestRecord = {
  method: string;
  url: string;
  pathname: string;
  search: string;
  kind: ShellRequestKind;
  /** Set only when `kind` is `"outside-allow-list"`, for the failure message. */
  reason?: string;
  /** True when #1070's enumerated gate would also have flagged this request. */
  enumeratedDataPlane: boolean;
};

/**
 * Classify one browser request against the authenticated app-shell allow-list.
 *
 * Only the pathname decides. `search` is recorded so a reviewer can see *why* a
 * request looks like a record fetch (`?kind=health`), but it never relaxes the
 * verdict: matching on pathname is what stops `/api/workspaces` from being
 * excused by a query string, and what stops `/api/search?kind=health` from
 * hiding behind a benign-looking parameter name.
 */
function classifyShellRequest(url: string, method = "GET"): ShellRequestRecord {
  const parsed = new URL(url, "http://localhost:3000");
  const pathname = parsed.pathname;
  const enumeratedDataPlane = ENUMERATED_DATA_PLANE_PATH.test(pathname);
  const base = {
    method,
    url,
    pathname,
    search: parsed.search,
    enumeratedDataPlane,
  };
  if (!pathname.includes("/api/")) {
    return { ...base, kind: "non-api" as const };
  }
  const isAuthSegment = AUTHENTICATED_SHELL_SEGMENT_PREFIXES.some((prefix) =>
    pathname.toLowerCase().startsWith(prefix.toLowerCase()),
  );
  const isExact = (AUTHENTICATED_SHELL_ALLOW_LIST as readonly string[]).includes(pathname);
  if (isExact || isAuthSegment) {
    return { ...base, kind: "allowed-shell" as const };
  }
  return {
    ...base,
    kind: "outside-allow-list" as const,
    reason: enumeratedDataPlane
      ? "enumerated data-plane path, not part of the authenticated app shell"
      : "unknown API path: not in AUTHENTICATED_SHELL_ALLOW_LIST",
  };
}

type ShellGate = {
  requests: () => ShellRequestRecord[];
  violations: () => ShellRequestRecord[];
  attach: (testInfo: import("@playwright/test").TestInfo, name: string) => Promise<void>;
};

/**
 * Record every request the page issues and classify it on the way in, so the
 * ledger and the verdict can never disagree.
 */
function installAuthenticatedShellGate(page: import("@playwright/test").Page): ShellGate {
  const requests: ShellRequestRecord[] = [];
  page.on("request", (request) => {
    requests.push(classifyShellRequest(request.url(), request.method()));
  });
  return {
    requests: () => requests,
    violations: () => requests.filter((entry) => entry.kind === "outside-allow-list"),
    attach: async (testInfo, name) => {
      await testInfo.attach(`${name}.requests.json`, {
        body: JSON.stringify(
          {
            policy: "deny-by-default over /api/*, allow-list by pathname",
            allowList: AUTHENTICATED_SHELL_ALLOW_LIST,
            recorded: requests,
            violations: requests.filter((entry) => entry.kind === "outside-allow-list"),
          },
          null,
          2,
        ),
        contentType: "application/json",
      });
    },
  };
}

/**
 * The synthetic record payload used by the negative control. It exists only
 * inside the `page.route` fulfilment below and is never written to disk, never
 * served by a real backend and never derived from a real record.
 */
const RECORD_CANARY = "SYNTHETIC-PATIENT-RECORD-4b8d1e07";
const syntheticSearchFixture = {
  items: [
    {
      id: "synthetic-record-0002",
      healthDocumentId: RECORD_CANARY,
      patientName: "Synthetic Patient",
      documentType: "lab-report",
      contentUrl: "/api/assets/synthetic-record-0002/content",
    },
  ],
  totalCount: 1,
};

/** Every `/api/*` path a pristine authenticated shell may fetch, and only those. */
const SYNTHETIC_SHELL_ROUTES: readonly string[] = AUTHENTICATED_SHELL_ALLOW_LIST;

/**
 * Mount the fake authenticated Health Care surface.
 *
 * The sentinel path is a stand-in for the real route document: it renders the
 * same kind of app shell (a heading plus passive bootstrap fetches) without
 * needing a backend, a session cookie or the #1070 fixture. `waitForNetworkIdle`
 * style settling is unnecessary because the caller drives the fetches
 * explicitly, which is what keeps this lane deterministic.
 */
async function mountSyntheticShell(page: import("@playwright/test").Page): Promise<void> {
  await page.route(`**${HEALTH_ROUTE}`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "text/html",
      body: [
        "<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Taslim.ai</title></head>",
        "<body><main aria-labelledby='health-care-title'><h1 id='health-care-title'>Health Care</h1>",
        "<p>does not collect, upload, store, or analyze health information</p></main></body></html>",
      ].join(""),
    });
  });
  for (const pathname of SYNTHETIC_SHELL_ROUTES) {
    await page.route(`**${pathname}*`, async (route) => {
      // Shape-agnostic: the shell bootstrap payloads differ, and none of them
      // may contain record content, which is the point of the leak check below.
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ ok: true, unreadCount: 0 }),
      });
    });
  }
}

/** Fire one fetch from inside the page so the browser network stack records it. */
async function fetchFromPage(page: import("@playwright/test").Page, href: string): Promise<string> {
  return page.evaluate(async (target) => {
    const response = await fetch(target);
    return await response.text();
  }, href);
}

test.describe("Health Care authenticated pre-consent API allow-list (#714, #1070 review follow-up)", () => {
  test("allows every known app-shell path and accounts for the whole list", () => {
    // The allow-list is not decorative: each entry must classify as allowed.
    const verdicts = AUTHENTICATED_SHELL_ALLOW_LIST.map((pathname) => ({
      pathname,
      kind: classifyShellRequest(`http://localhost:3000${pathname}?workspaceId=synthetic-ws`).kind,
    }));
    expect(
      verdicts.filter((entry) => entry.kind !== "allowed-shell"),
      `app-shell paths rejected by the gate: ${JSON.stringify(verdicts)}`,
    ).toEqual([]);
    // Auth segment family stays open (session bootstrap carries ids), and the
    // query string never changes a verdict.
    expect(classifyShellRequest("http://localhost:3000/api/auth/logout").kind).toBe("allowed-shell");
    expect(classifyShellRequest("http://localhost:3000/api/auth/me").kind).toBe(
      classifyShellRequest("http://localhost:3000/api/auth/me?x=1&kind=health").kind,
    );
    // Non-API traffic is out of scope for an API gate; it is not silently
    // counted as allowed either, so a reviewer can see the three-way split.
    expect(classifyShellRequest("http://localhost:3000/_next/static/css/app.css").kind).toBe("non-api");
    expect(classifyShellRequest(`http://localhost:3000${HEALTH_ROUTE}`).kind).toBe("non-api");
  });

  test("fails closed on generic record-bearing endpoints the enumerated #1070 gate misses", () => {
    // The exact inputs named in the review, plus the neighbours a component
    // could reach for on the same surface.
    const genericRecordBearing = [
      "/api/search?kind=health",
      "/api/generation/jobs/synthetic-job-1",
      "/api/graphql",
      "/api/workspaces/synthetic-ws/assets",
      "/api/projects/synthetic-p-1/overview",
    ];
    const missed = genericRecordBearing.filter((href) => {
      const record = classifyShellRequest(`http://localhost:3000${href}`);
      return record.kind !== "outside-allow-list";
    });
    expect(missed, `a record-bearing endpoint passed the allow-list: ${JSON.stringify(missed)}`).toEqual([]);

    // This is the gap statement, expressed as an executable difference rather
    // than a comment: the enumerated prefix regex does not see the first entry,
    // the pathname allow-list does.
    const searchHref = "/api/search?kind=health";
    const enumeratedGateWouldFlag = ENUMERATED_DATA_PLANE_PATH.test(
      new URL(`http://localhost:3000${searchHref}`).pathname,
    );
    expect(enumeratedGateWouldFlag, "premise of this lane changed: /api/search is now enumerated").toBe(false);
    expect(classifyShellRequest(`http://localhost:3000${searchHref}`).kind).toBe("outside-allow-list");

    // Specificity in the other direction: an enumerated data-plane path is
    // still a violation here, so this lane is a superset of #1070's check and
    // not a replacement for it.
    for (const href of ["/api/health/documents", "/api/patients/synthetic/records", "/api/assets/x/content"]) {
      const record = classifyShellRequest(`http://localhost:3000${href}`);
      expect(record.kind, `${href} must fail closed`).toBe("outside-allow-list");
      expect(record.enumeratedDataPlane, `${href} is the overlap with #1070`).toBe(true);
    }

    // A benign name is not an exemption: `/create/health`-adjacent paths that
    // merely *look* like the route are non-API and stay out of the ledger.
    expect(classifyShellRequest("http://localhost:3000/create/health/preview").kind).toBe("non-api");
    // But an `/api` path that only *looks* allow-listed by prefix is flagged.
    expect(classifyShellRequest("http://localhost:3000/api/workspaces/synthetic-ws").kind).toBe(
      "outside-allow-list",
    );
    expect(classifyShellRequest("http://localhost:3000/api/auth/me/records").kind).toBe("allowed-shell");
  });

  test("a pristine synthetic authenticated navigation stays inside the allow-list", async ({ page }, testInfo) => {
    const gate = installAuthenticatedShellGate(page);
    await mountSyntheticShell(page);
    await page.goto(HEALTH_ROUTE);
    await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toBeVisible();
    // Replay the bootstrap the real shell performs, in its real method shapes.
    for (const pathname of SYNTHETIC_SHELL_ROUTES) {
      await fetchFromPage(page, pathname);
    }
    await expect(
      gate.violations(),
      `the shell bootstrap itself escaped the allow-list: ${JSON.stringify(gate.violations())}`,
    ).toEqual([]);
    // Control for the lane below: this run must actually have recorded the
    // allow-listed API traffic, otherwise "zero violations" is vacuous.
    const allowed = gate.requests().filter((entry) => entry.kind === "allowed-shell");
    const seenPathnames = new Set(allowed.map((entry) => entry.pathname));
    expect(
      [...SYNTHETIC_SHELL_ROUTES].filter((pathname) => !seenPathnames.has(pathname)),
      "the pristine control measured nothing",
    ).toEqual([]);
    // And the app shell it replayed carries no record content.
    const served = await page.content();
    expect(served, "the synthetic shell carried the record canary").not.toContain(RECORD_CANARY);
    await gate.attach(testInfo, "pristine-authenticated-shell");
  });

  test("negative control: one injected generic /api/search?kind=health call trips the gate", async ({ page }, testInfo) => {
    const gate = installAuthenticatedShellGate(page);
    await mountSyntheticShell(page);
    // The generic record endpoint is fulfilled locally with the canary, so the
    // injection is answered without a backend and without real data.
    await page.route("**/api/search**", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(syntheticSearchFixture),
      });
    });
    await page.goto(HEALTH_ROUTE);
    for (const pathname of SYNTHETIC_SHELL_ROUTES) {
      await fetchFromPage(page, pathname);
    }
    expect(gate.violations(), "pristine baseline before the injection").toEqual([]);

    // Mutation: exactly one generic, non-enumerated, record-bearing request —
    // the shape the #1070 review said lane 4 could not see.
    const payload = await fetchFromPage(page, "/api/search?kind=health");
    expect(payload, "the fake generic endpoint answered locally").toContain(RECORD_CANARY);

    const violations = gate.violations();
    expect(violations, "the authenticated gate stayed green on a generic record fetch").not.toEqual([]);
    expect(violations.map((entry) => entry.pathname), "the named violation is the injected request").toEqual([
      "/api/search",
    ]);
    expect(violations[0].search, "the health-shaped query is preserved for the reviewer").toBe("?kind=health");
    expect(violations[0].kind).toBe("outside-allow-list");
    expect(violations[0].enumeratedDataPlane, "this is the request #1070's gate misses").toBe(false);
    expect(violations[0].reason).toContain("unknown API path");

    // The DOM is unchanged by the injection: the failure is transport-only,
    // which is precisely the class of regression a rendered-output assertion
    // cannot catch.
    const served = await page.content();
    expect(served, "the canary leaked into the DOM, so this control is not transport-only").not.toContain(
      RECORD_CANARY,
    );
    await gate.attach(testInfo, "negative-control-generic-endpoint");
  });
});
