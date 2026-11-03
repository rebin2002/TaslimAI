import { randomUUID } from "node:crypto";
import type { BrowserContext, Locator, Page, Request, Route } from "@playwright/test";
import { test, expect, apiJson, E2E_API_URL, registerInUi, type TestUser } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";
const syntheticSessionMode = process.env.E2E_SYNTHETIC_AUTH === "1";
const requireProductionPrefetch = process.env.E2E_EXPECT_PRODUCTION_PREFETCH === "1";
const conversationId = "11111111-1111-4111-8111-111111111111";
const stamp = "7F3A";
const privateMarkers = [
  `PRIVATE_CHAT_SENTINEL_${stamp}`,
  `PRIVATE_CHAT_BODY_SENTINEL_${stamp}`,
  `PRIVATE_DOCUMENT_SENTINEL_${stamp}`,
  `PRIVATE_IMAGE_SENTINEL_${stamp}`,
  `PRIVATE_PRESENTATION_SENTINEL_${stamp}`,
  `PRIVATE_PROJECT_SENTINEL_${stamp}`,
  `PRIVATE_FILE_SENTINEL_${stamp}.pdf`,
];

type StudioCase = {
  name: string;
  path: string;
  sourcePath: string;
  navKind: "link" | "chat-button";
  assetType: "document" | "image" | "presentation";
  markers: string[];
};

const studios: StudioCase[] = [
  {
    name: "Chat",
    path: `/chat/${conversationId}`,
    sourcePath: "/chat",
    navKind: "chat-button",
    assetType: "document",
    markers: [`PRIVATE_CHAT_SENTINEL_${stamp}`, `PRIVATE_CHAT_BODY_SENTINEL_${stamp}`],
  },
  {
    name: "Document",
    path: "/create/document",
    sourcePath: "/create",
    navKind: "link",
    assetType: "document",
    markers: [`PRIVATE_DOCUMENT_SENTINEL_${stamp}`, `PRIVATE_FILE_SENTINEL_${stamp}.pdf`, `PRIVATE_PROJECT_SENTINEL_${stamp}`],
  },
  {
    name: "Image",
    path: "/create/image",
    sourcePath: "/create",
    navKind: "link",
    assetType: "image",
    markers: [`PRIVATE_IMAGE_SENTINEL_${stamp}`, `PRIVATE_PROJECT_SENTINEL_${stamp}`],
  },
  {
    name: "Presentation",
    path: "/create/presentation",
    sourcePath: "/create",
    navKind: "link",
    assetType: "presentation",
    markers: [`PRIVATE_PRESENTATION_SENTINEL_${stamp}`, `PRIVATE_FILE_SENTINEL_${stamp}.pdf`, `PRIVATE_PROJECT_SENTINEL_${stamp}`],
  },
];

type RequestRow = {
  request: Request;
  url: string;
  path: string;
  method: string;
  time: number;
  rsc: boolean;
  prefetch: boolean;
  navigation: boolean;
  status?: number;
  body?: Promise<string>;
};

type Monitor = {
  rows: RequestRow[];
  byRequest: Map<Request, RequestRow>;
  externalAttempts: string[];
  generationPosts: string[];
  revokedAt: number;
};

function assertLoopback(value: string, label: string) {
  const hostname = new URL(value).hostname.replace(/^\[|\]$/g, "");
  if (!["localhost", "127.0.0.1", "::1"].includes(hostname)) {
    throw new Error(`${label} must be a loopback URL for this test; got ${hostname}`);
  }
}

function isLoopback(hostname: string) {
  const normalized = hostname.replace(/^\[|\]$/g, "");
  return ["localhost", "127.0.0.1", "::1"].includes(normalized);
}

function isProtectedRead(method: string, path: string) {
  return method === "GET" && (
    path.startsWith("/api/workspaces/")
    || path.startsWith("/api/conversations/")
    || path.startsWith("/api/assets")
    || path.startsWith("/api/files")
  );
}

function isGenerationPost(method: string, path: string) {
  return method === "POST" && /generation|provider|inference/i.test(path);
}

function isPrefetch(request: Request) {
  const headers = request.headers();
  return headers["next-router-prefetch"] === "1" || /prefetch/i.test(headers.purpose ?? "");
}

function observePage(page: Page, monitor: Monitor) {
  page.on("request", request => {
    const parsed = new URL(request.url());
    const row: RequestRow = {
      request,
      url: request.url(),
      path: parsed.pathname,
      method: request.method(),
      time: Date.now(),
      rsc: parsed.searchParams.has("_rsc"),
      prefetch: isPrefetch(request),
      navigation: request.isNavigationRequest(),
    };
    monitor.rows.push(row);
    monitor.byRequest.set(request, row);
    if (isGenerationPost(row.method, row.path)) monitor.generationPosts.push(row.path);
  });
  page.on("response", response => {
    const row = monitor.byRequest.get(response.request());
    if (!row) return;
    row.status = response.status();
    if (row.rsc || isProtectedRead(row.method, row.path)) {
      row.body = response.text().catch(() => "<unreadable-response-body>");
    }
  });
}

function privateProject(workspaceId: string) {
  return {
    id: "project-private-revocation-fixture",
    workspaceId,
    name: `PRIVATE_PROJECT_SENTINEL_${stamp}`,
    description: "Synthetic private project used only by this Playwright test.",
    instructions: null,
    contextNotes: null,
    type: "Business",
    status: "Active",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    archivedAt: null,
  };
}

function privateFile(workspaceId: string) {
  return {
    id: "file-private-revocation-fixture",
    workspaceId,
    projectId: "project-private-revocation-fixture",
    conversationId: null,
    originalFileName: `PRIVATE_FILE_SENTINEL_${stamp}.pdf`,
    contentType: "application/pdf",
    extension: ".pdf",
    sizeBytes: 32,
    storageProvider: "Synthetic",
    status: "Ready",
    textExtractionStatus: "Ready",
    extractedTextLength: 12,
    createdAt: new Date().toISOString(),
    processedAt: new Date().toISOString(),
  };
}

function privateAsset(workspaceId: string, assetType: StudioCase["assetType"]) {
  const name = `PRIVATE_${assetType.toUpperCase()}_SENTINEL_${stamp}`;
  return {
    id: `asset-${assetType}-revocation-fixture`,
    workspaceId,
    projectId: "project-private-revocation-fixture",
    projectName: `PRIVATE_PROJECT_SENTINEL_${stamp}`,
    name: assetType === "document" ? `PRIVATE_DOCUMENT_SENTINEL_${stamp}` : assetType === "image" ? `PRIVATE_IMAGE_SENTINEL_${stamp}` : `PRIVATE_PRESENTATION_SENTINEL_${stamp}`,
    description: "Synthetic private asset; no backing provider or external file is used.",
    assetType,
    mimeType: "application/vnd.taslim.synthetic",
    status: "Active",
    hasFile: false,
    canPreview: false,
    fileSizeBytes: 0,
    sourceStudio: assetType,
    sourceJobTitle: name,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    archivedAt: null,
    representations: [],
  };
}

function privateConversation(workspaceId: string) {
  return {
    id: conversationId,
    workspaceId,
    projectId: "project-private-revocation-fixture",
    title: `PRIVATE_CHAT_SENTINEL_${stamp}`,
    status: "Active",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastMessageAt: new Date().toISOString(),
  };
}

function jsonResponse(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: "application/json; charset=utf-8",
    headers: { "cache-control": "no-store, private" },
    body: JSON.stringify(body),
  });
}

async function installLocalOnlyRoutes(context: BrowserContext, monitor: Monitor, studio: StudioCase, workspaceId: string, isRevoked: () => boolean) {
  await context.route("**/api/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (!isLoopback(url.hostname)) {
      monitor.externalAttempts.push(url.href);
      await route.abort("blockedbyclient");
      return;
    }

    if (isGenerationPost(request.method(), url.pathname)) {
      monitor.generationPosts.push(url.pathname);
      await route.abort("blockedbyclient");
      return;
    }

    if (isRevoked() || !isProtectedRead(request.method(), url.pathname)) {
      await route.continue();
      return;
    }

    const conversation = privateConversation(workspaceId);
    if (url.pathname === `/api/workspaces/${workspaceId}/conversations`) return jsonResponse(route, [conversation]);
    if (url.pathname === `/api/conversations/${conversationId}`) return jsonResponse(route, conversation);
    if (url.pathname === `/api/conversations/${conversationId}/messages`) {
      return jsonResponse(route, [{
        id: "message-private-revocation-fixture",
        conversationId,
        role: "Assistant",
        content: `PRIVATE_CHAT_BODY_SENTINEL_${stamp}`,
        status: "Completed",
        createdAt: new Date().toISOString(),
        sequence: 1,
      }]);
    }
    if (url.pathname === `/api/workspaces/${workspaceId}/projects`) {
      return jsonResponse(route, url.searchParams.get("status") === "Archived" ? [] : [privateProject(workspaceId)]);
    }
    if (url.pathname === `/api/workspaces/${workspaceId}/files`) return jsonResponse(route, [privateFile(workspaceId)]);
    if (url.pathname === `/api/workspaces/${workspaceId}/assets` || url.pathname === "/api/assets") {
      const asset = privateAsset(workspaceId, studio.assetType);
      return jsonResponse(route, { items: [asset], page: 1, pageSize: 6, totalCount: 1, totalPages: 1 });
    }
    await route.continue();
  });
}

async function addExternalEgressBlock(context: BrowserContext, monitor: Monitor) {
  await context.route("**/*", async route => {
    const url = new URL(route.request().url());
    if (["http:", "https:"].includes(url.protocol) && !isLoopback(url.hostname)) {
      monitor.externalAttempts.push(url.href);
      await route.abort("blockedbyclient");
      return;
    }
    await route.continue();
  });
}

async function createSession(page: Page, request: import("@playwright/test").APIRequestContext, testUser: TestUser) {
  assertLoopback(webUrl, "E2E_WEB_URL");
  assertLoopback(E2E_API_URL, "E2E_API_URL");
  if (syntheticSessionMode) {
    const token = `synthetic-${randomUUID()}`;
    const response = await request.post(`${E2E_API_URL}/__test__/session`, { data: { token, displayName: "Synthetic Integration Owner" } });
    expect(response.status()).toBe(201);
    await page.context().addCookies([{ name: "taslim.auth", value: token, url: webUrl, httpOnly: true, sameSite: "Lax" }]);
    return token;
  }
  await registerInUi(page, testUser, true);
  const cookie = (await page.context().cookies(webUrl)).find(item => item.name === "taslim.auth");
  if (!cookie) throw new Error("The local E2E registration did not issue taslim.auth.");
  return cookie.value;
}

async function targetControl(page: Page, studio: StudioCase): Promise<Locator> {
  if (studio.navKind === "chat-button") return page.getByRole("button", { name: new RegExp(`PRIVATE_CHAT_SENTINEL_${stamp}`) }).first();
  return page.locator(`a[href="${studio.path}"]`).first();
}

function makeMonitor(): Monitor {
  return { rows: [], byRequest: new Map(), externalAttempts: [], generationPosts: [], revokedAt: 0 };
}

async function markerText(page: Page) {
  return page.locator("body").innerText().catch(() => "");
}

async function noPrivateMarkerVisible(page: Page, studio: StudioCase) {
  const text = await markerText(page);
  return studio.markers.filter(marker => text.includes(marker));
}

for (const studio of studios) {
  test(`${studio.name}: anonymous and expired/unknown session responses never render Studio markers`, async ({ browser, request }) => {
    test.setTimeout(60_000);
    assertLoopback(webUrl, "E2E_WEB_URL");
    assertLoopback(E2E_API_URL, "E2E_API_URL");
    const context = await browser.newContext({ baseURL: webUrl, ignoreHTTPSErrors: syntheticSessionMode, viewport: { width: 1365, height: 900 } });
    const monitor = makeMonitor();
    try {
      await addExternalEgressBlock(context, monitor);
      const page = await context.newPage();
      const anonymous = await request.get(new URL(studio.path, webUrl).toString(), { maxRedirects: 0 });
      expect(anonymous.status()).toBe(307);
      expect(anonymous.headers().location).toBe(`/login?next=${encodeURIComponent(studio.path)}`);
      const anonymousBody = await anonymous.text();
      for (const marker of studio.markers) expect(anonymousBody).not.toContain(marker);

      const token = `expired-${randomUUID()}`;
      if (syntheticSessionMode) {
        const created = await request.post(`${E2E_API_URL}/__test__/session`, { data: { token, displayName: "Expired Synthetic Owner" } });
        expect(created.status()).toBe(201);
        const expired = await request.post(`${E2E_API_URL}/__test__/expire-session`, { data: { token } });
        expect(expired.status()).toBe(200);
      }
      await context.addCookies([{ name: "taslim.auth", value: token, url: webUrl, httpOnly: true, sameSite: "Lax" }]);
      const apiSession = await request.get(`${E2E_API_URL}/api/auth/me`, { headers: { Cookie: `taslim.auth=${token}` } });
      expect(apiSession.status()).toBe(401);

      const expiredResponse = await request.get(new URL(studio.path, webUrl).toString(), {
        maxRedirects: 0,
        headers: { Cookie: `taslim.auth=${token}` },
      });
      expect(expiredResponse.status()).toBe(307);
      expect(expiredResponse.headers().location).toBe(`/login?next=${encodeURIComponent(studio.path)}`);
      const expiredBody = await expiredResponse.text();
      for (const marker of studio.markers) expect(expiredBody).not.toContain(marker);

      await page.goto(studio.path);
      await expect(page).toHaveURL(new RegExp(`/login\\?next=${encodeURIComponent(studio.path).replaceAll("%", "%")}$`));
      for (const marker of studio.markers) await expect(page.locator("body")).not.toContainText(marker);
      if (!syntheticSessionMode) {
        test.info().annotations.push({ type: "expiry-boundary", description: "The existing CI API receives an unknown stale token and returns 401; a true expired-session row is exercised with the local synthetic API mode." });
      }
      expect(monitor.externalAttempts).toEqual([]);
      expect(monitor.generationPosts).toEqual([]);
    } finally {
      await context.close();
    }
  });

  test(`${studio.name}: authorized private state is denied after revocation across navigation, history, reload, and a second tab`, async ({ browser, request, testUser }) => {
    test.setTimeout(120_000);
    assertLoopback(webUrl, "E2E_WEB_URL");
    assertLoopback(E2E_API_URL, "E2E_API_URL");
    const context = await browser.newContext({ baseURL: webUrl, ignoreHTTPSErrors: syntheticSessionMode, viewport: { width: 1365, height: 900 } });
    const monitor = makeMonitor();
    let revoked = false;
    try {
      await addExternalEgressBlock(context, monitor);
      const pageA = await context.newPage();
      const token = await createSession(pageA, request, testUser);
      const auth = await apiJson<{ personalWorkspace: { id: string } }>(pageA.request, "GET", "/api/auth/me");
      const workspaceId = auth.personalWorkspace.id;
      await installLocalOnlyRoutes(context, monitor, studio, workspaceId, () => revoked);
      observePage(pageA, monitor);

      const authorizedResponse = await pageA.goto(studio.path);
      expect(authorizedResponse?.status()).toBe(200);
      await expect.poll(async () => (await markerText(pageA)).includes(studio.markers[0]), { timeout: 15_000 }).toBe(true);

      const pageB = await context.newPage();
      observePage(pageB, monitor);
      const pageBResponse = await pageB.goto(studio.sourcePath);
      expect(pageBResponse?.status()).toBe(200);
      const controlB = await targetControl(pageB, studio);
      await expect(controlB).toBeVisible();

      let preRevocationPrefetches = 0;
      if (studio.navKind === "link") {
        await controlB.hover();
        await pageB.waitForTimeout(900);
        preRevocationPrefetches = monitor.rows.filter(row => row.path === studio.path && row.rsc && row.prefetch).length;
        if (requireProductionPrefetch) expect(preRevocationPrefetches).toBeGreaterThan(0);
      } else {
        test.info().annotations.push({ type: "navigation-shape", description: "Chat conversation selection uses router.push from a button rather than a Next Link; this case exercises the real client transition and browser history, not native Link prefetch for the dynamic conversation URL." });
      }

      const priorCookie = (await context.cookies(webUrl)).find(cookie => cookie.name === "taslim.auth");
      expect(priorCookie).toBeDefined();
      if (!priorCookie) throw new Error("Missing pre-revocation session cookie.");
      await apiJson(pageA.request, "POST", "/api/auth/sessions/revoke");
      const freshCookie = (await context.cookies(webUrl)).find(cookie => cookie.name === "taslim.auth");
      expect(freshCookie).toBeDefined();
      if (!freshCookie) throw new Error("Session revocation did not rotate the local session cookie.");
      expect((await pageA.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(200);

      // Restore the old cookie in both tabs. The server has revoked it, but this
      // models another already-open tab retaining its pre-revocation client state.
      await context.addCookies([priorCookie]);
      expect((await pageA.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(401);
      revoked = true;
      monitor.revokedAt = Date.now();

      // Independent control: the local auth API must deny the old cookie. This
      // explicit test request is not counted as an automatic browser request.
      const deniedControl = await request.get(`${E2E_API_URL}/api/workspaces/${workspaceId}/projects`, {
        headers: { Cookie: `taslim.auth=${token}` },
      });
      expect([401, 403]).toContain(deniedControl.status());
      const deniedControlBody = await deniedControl.text();
      for (const marker of privateMarkers) expect(deniedControlBody).not.toContain(marker);

      const activeTabMarkers = await noPrivateMarkerVisible(pageA, studio);
      const secondTabMarkers = await noPrivateMarkerVisible(pageB, studio);
      expect.soft(activeTabMarkers, `${studio.name} private state remained visible in the already-open tab immediately after session revocation`).toEqual([]);
      expect.soft(secondTabMarkers, `${studio.name} private state remained visible in the second tab immediately after session revocation`).toEqual([]);

      await controlB.click();
      await pageB.waitForTimeout(500);
      const afterNavigation = await noPrivateMarkerVisible(pageB, studio);
      expect.soft(afterNavigation, `${studio.name} marker visible after revoked-session client navigation`).toEqual([]);

      await pageA.goBack({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageA.waitForTimeout(250);
      expect.soft(await noPrivateMarkerVisible(pageA, studio), `${studio.name} marker visible after back`).toEqual([]);
      await pageA.goForward({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageA.waitForTimeout(250);
      expect.soft(await noPrivateMarkerVisible(pageA, studio), `${studio.name} marker visible after forward`).toEqual([]);

      await pageB.goBack({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageB.waitForTimeout(200);
      expect.soft(await noPrivateMarkerVisible(pageB, studio), `${studio.name} marker visible in second-tab back history`).toEqual([]);
      await pageB.goForward({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageB.waitForTimeout(200);
      expect.soft(await noPrivateMarkerVisible(pageB, studio), `${studio.name} marker visible in second-tab forward history`).toEqual([]);

      await pageA.reload({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageA.waitForTimeout(250);
      expect.soft(await noPrivateMarkerVisible(pageA, studio), `${studio.name} marker visible after reload`).toEqual([]);
      await pageB.reload({ waitUntil: "domcontentloaded" }).catch(() => null);
      await pageB.waitForTimeout(250);
      expect.soft(await noPrivateMarkerVisible(pageB, studio), `${studio.name} marker visible after second-tab reload`).toEqual([]);

      const pageC = await context.newPage();
      observePage(pageC, monitor);
      if (studio.navKind === "link") {
        await pageC.goto(studio.sourcePath);
        const controlC = await targetControl(pageC, studio);
        await expect(controlC).toBeVisible();
        await controlC.hover();
        await pageC.waitForTimeout(700);
        await controlC.click();
      } else {
        await pageC.goto(studio.path);
      }
      await pageC.waitForTimeout(250);
      expect.soft(await noPrivateMarkerVisible(pageC, studio), `${studio.name} marker visible in a new tab after revocation`).toEqual([]);

      // Allow in-flight responses to settle before classifying the two contracts.
      await pageA.waitForTimeout(400);
      await pageB.waitForTimeout(400);
      await pageC.waitForTimeout(200);
      const afterRevokeRows = monitor.rows.filter(row => row.time >= monitor.revokedAt);
      const protectedGets = afterRevokeRows.filter(row => isProtectedRead(row.method, row.path));
      const completedProtectedGets = protectedGets.filter(row => row.status !== undefined);
      const correctlyDenied = completedProtectedGets.filter(row => row.status === 401 || row.status === 403);
      const nonDenied = completedProtectedGets.filter(row => row.status !== 401 && row.status !== 403);
      const targetPrefetches = studio.navKind === "link"
        ? afterRevokeRows.filter(row => row.path === studio.path && row.rsc && row.prefetch)
        : [];
      const privateResponseBodies: Array<{ path: string; status?: number }> = [];
      const rscResponseAudit: Array<{ path: string; status?: number; prefetch: boolean; privateMarker: boolean }> = [];
      for (const row of afterRevokeRows) {
        if (!row.body) continue;
        const body = await row.body;
        const hasPrivateMarker = privateMarkers.some(marker => body.includes(marker));
        if (row.rsc) rscResponseAudit.push({ path: row.path, status: row.status, prefetch: row.prefetch, privateMarker: hasPrivateMarker });
        if (hasPrivateMarker) privateResponseBodies.push({ path: row.path, status: row.status });
      }
      const evidence = {
        studio: studio.name,
        syntheticLocalAuth: syntheticSessionMode,
        initialAuthorizedStatus: authorizedResponse?.status(),
        privateMarkerVisibleBeforeRevocation: true,
        activeTabMarkersImmediatelyAfterRevocation: activeTabMarkers,
        secondTabMarkersImmediatelyAfterRevocation: secondTabMarkers,
        preRevocationNativeLinkPrefetches: preRevocationPrefetches,
        nativeLinkPrefetchApplicable: studio.navKind === "link",
        explicitRevokedApiControlStatus: deniedControl.status(),
        postRevocationProtectedGetAttempts: protectedGets.length,
        correctlyDeniedProtectedGetResponses: correctlyDenied.length,
        postRevocationProtectedGetResponses: completedProtectedGets.map(row => ({ path: row.path, status: row.status })),
        nonDeniedProtectedGetResponses: nonDenied.map(row => ({ path: row.path, status: row.status })),
        postRevocationTargetPrefetches: targetPrefetches.map(row => ({ path: row.path, status: row.status })),
        postRevocationRscResponses: rscResponseAudit,
        privateResponseBodies,
        generationPosts: monitor.generationPosts,
        blockedExternalAttempts: monitor.externalAttempts,
      };
      console.log(`STUDIOS_REVOKED_SESSION_EVIDENCE ${JSON.stringify(evidence)}`);

      // HTTP authorization is a separate invariant from avoiding stale calls.
      expect.soft(nonDenied.map(row => ({ path: row.path, status: row.status })), "Any post-revocation protected GET that reaches the local API must be denied with 401/403.").toEqual([]);
      expect.soft(privateResponseBodies, "No private marker may be served in a post-revocation API or Flight response.").toEqual([]);
      expect.soft(monitor.generationPosts, "No generation/provider POST is allowed in this auth regression.").toEqual([]);
      expect.soft(monitor.externalAttempts, "All browser egress outside loopback is blocked and should remain unused.").toEqual([]);

      // The next two are intentionally stricter performance/privacy contracts:
      // correctly denied 401/403 requests above still fail these zero-request checks.
      expect.soft(protectedGets.map(row => ({ path: row.path, status: row.status })), "Strict contract: zero protected API GET attempts after revocation (separate from 401/403 denial).").toEqual([]);
      if (studio.navKind === "link" && requireProductionPrefetch) {
        expect.soft(targetPrefetches.map(row => row.status), "Strict contract: zero target Link prefetches after revocation.").toEqual([]);
      }
    } finally {
      await context.close();
    }
  });
}
