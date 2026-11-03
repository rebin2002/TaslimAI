import { randomUUID } from "node:crypto";
import type { BrowserContext, Locator, Page, Request, Route } from "@playwright/test";
import { test, expect, apiJson, E2E_API_URL, loginInUi, registerInUi, type TestUser } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";
const syntheticSessionMode = process.env.E2E_SYNTHETIC_AUTH === "1";
const requireProductionPrefetch = process.env.E2E_EXPECT_PRODUCTION_PREFETCH === "1";
const responseBodyReadTimeoutMs = 5_000;
const responseBodyReadTimeoutMarker = "<response-body-read-timeout>";
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

type TestAccount = "A" | "B";
type AccountState = TestAccount | "loggedOut";

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
  accountAtRequest?: AccountState;
  status?: number;
  body?: Promise<string>;
  bodyReadStatus?: "read" | "timeout" | "error";
  bodyReadError?: string;
  failure?: string;
};

type Monitor = {
  rows: RequestRow[];
  byRequest: Map<Request, RequestRow>;
  externalAttempts: string[];
  generationPosts: string[];
  blockedGenerationPosts: string[];
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
    path === "/api/workspaces"
    || path.startsWith("/api/workspaces/")
    || path.startsWith("/api/conversations/")
    || path.startsWith("/api/assets")
    || path.startsWith("/api/files")
    || path === "/api/generation/jobs"
    || path.startsWith("/api/generation/jobs/")
    || path.startsWith("/api/research-generation/jobs/")
    || /^\/api\/projects\/[^/]+(?:\/overview)?\/?$/i.test(path)
  );
}

function isGenerationPost(method: string, path: string) {
  const chargeableChatPath = /^\/api\/conversations\/[^/]+\/messages(?:\/stream|\/[^/]+\/regenerate)?\/?$/i.test(path);
  return method === "POST" && (chargeableChatPath || /generation|provider|inference/i.test(path));
}

function isPrefetch(request: Request) {
  const headers = request.headers();
  return headers["next-router-prefetch"] === "1" || /prefetch/i.test(headers.purpose ?? "");
}

function observePage(page: Page, monitor: Monitor, accountAtRequest?: () => AccountState) {
  page.on("request", request => {
    const parsed = new URL(request.url());
    const row: RequestRow = {
      request,
      url: request.url(),
      path: parsed.pathname,
      method: request.method(),
      time: performance.now(),
      rsc: parsed.searchParams.has("_rsc"),
      prefetch: isPrefetch(request),
      navigation: request.isNavigationRequest(),
      accountAtRequest: accountAtRequest?.(),
    };
    monitor.rows.push(row);
    monitor.byRequest.set(request, row);
    if (isGenerationPost(row.method, row.path)) monitor.generationPosts.push(row.path);
  });
  const captureResponse = (request: Request, response: import("@playwright/test").Response) => {
    const row = monitor.byRequest.get(request)
      ?? [...monitor.rows].reverse().find(candidate => candidate.status === undefined
        && candidate.method === request.method() && candidate.url === request.url());
    if (!row) return;
    row.status = response.status();
    if ((row.rsc || isProtectedRead(row.method, row.path)) && !row.body) {
      row.body = new Promise(resolve => {
        let settled = false;
        const timer: { handle?: ReturnType<typeof setTimeout> } = {};
        const finish = (status: "read" | "timeout" | "error", body: string) => {
          if (settled) return;
          settled = true;
          if (timer.handle) clearTimeout(timer.handle);
          row.bodyReadStatus = status;
          resolve(body);
        };
        timer.handle = setTimeout(() => finish("timeout", responseBodyReadTimeoutMarker), responseBodyReadTimeoutMs);
        void response.text()
          .then(body => finish("read", body))
          .catch(error => {
            row.bodyReadError = (error instanceof Error ? error.message : String(error)).slice(0, 180);
            finish("error", "<unreadable-response-body>");
          });
      });
    }
  };
  page.on("response", response => captureResponse(response.request(), response));
  page.on("requestfinished", async request => {
    const row = monitor.byRequest.get(request)
      ?? [...monitor.rows].reverse().find(candidate => candidate.status === undefined
        && candidate.method === request.method() && candidate.url === request.url());
    if (!row || row.status !== undefined) return;
    const response = await request.response();
    if (response) captureResponse(request, response);
  });
  page.on("requestfailed", request => {
    const row = monitor.byRequest.get(request)
      ?? [...monitor.rows].reverse().find(candidate => candidate.status === undefined
        && candidate.method === request.method() && candidate.url === request.url());
    if (row) row.failure = request.failure()?.errorText ?? "request failed";
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

const accountProjectIds: Record<TestAccount, string> = {
  A: "project-private-account-a",
  B: "project-private-account-b",
};
const accountConversationIds: Record<TestAccount, string> = {
  A: conversationId,
  B: "22222222-2222-4222-8222-222222222222",
};

function accountMarker(account: TestAccount, label: string) {
  return `ACCOUNT_${account}_PRIVATE_${label}_SENTINEL_${stamp}`;
}

function accountStudioMarker(account: TestAccount, studio: StudioCase) {
  return accountMarker(account, studio.name.toUpperCase());
}

function accountMarkers(account: TestAccount, studio: StudioCase) {
  return [
    accountStudioMarker(account, studio),
    accountMarker(account, "PROJECT"),
    `${accountMarker(account, "FILE")}.pdf`,
    accountMarker(account, "CHAT_BODY"),
  ];
}

function accountStudioPath(account: TestAccount, studio: StudioCase) {
  return studio.name === "Chat" ? `/chat/${accountConversationIds[account]}` : studio.path;
}

function privateAccountProject(workspaceId: string, account: TestAccount) {
  return {
    id: accountProjectIds[account],
    workspaceId,
    name: accountMarker(account, "PROJECT"),
    description: "Synthetic account-specific private project for the logout/login regression.",
    instructions: null,
    contextNotes: null,
    type: "Business",
    status: "Active",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    archivedAt: null,
  };
}

function privateAccountFile(workspaceId: string, account: TestAccount) {
  return {
    id: `file-private-account-${account.toLowerCase()}`,
    workspaceId,
    projectId: accountProjectIds[account],
    conversationId: null,
    originalFileName: `${accountMarker(account, "FILE")}.pdf`,
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

function privateAccountAsset(workspaceId: string, assetType: StudioCase["assetType"], account: TestAccount) {
  const name = accountMarker(account, assetType.toUpperCase());
  return {
    id: `asset-${assetType}-account-${account.toLowerCase()}`,
    workspaceId,
    projectId: accountProjectIds[account],
    projectName: accountMarker(account, "PROJECT"),
    name,
    description: "Synthetic, account-specific private asset; no backing provider is used.",
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

function privateAccountConversation(workspaceId: string, account: TestAccount) {
  return {
    id: accountConversationIds[account],
    workspaceId,
    projectId: accountProjectIds[account],
    title: accountMarker(account, "CHAT"),
    status: "Active",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastMessageAt: new Date().toISOString(),
  };
}

function privateAccountMessages(account: TestAccount) {
  return [{
    id: `message-private-account-${account.toLowerCase()}`,
    conversationId: accountConversationIds[account],
    role: "Assistant",
    content: accountMarker(account, "CHAT_BODY"),
    status: "Completed",
    createdAt: new Date().toISOString(),
    sequence: 1,
  }];
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

async function installAccountSwitchNetworkGuard(
  context: BrowserContext,
  monitor: Monitor,
  studio: StudioCase,
  activeAccount: () => AccountState,
  workspaceIds: Record<TestAccount, string>,
) {
  await context.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (["http:", "https:"].includes(url.protocol) && !isLoopback(url.hostname)) {
      monitor.externalAttempts.push(url.href);
      await route.abort("blockedbyclient");
      return;
    }
    if (isGenerationPost(request.method(), url.pathname)) {
      monitor.generationPosts.push(url.pathname);
      monitor.blockedGenerationPosts.push(url.pathname);
      await route.abort("blockedbyclient");
      return;
    }
    if (syntheticSessionMode || request.method() !== "GET" || !isProtectedRead(request.method(), url.pathname)) {
      await route.continue();
      return;
    }

    const account = activeAccount();
    if (account === "loggedOut") {
      await route.continue();
      return;
    }
    const workspaceId = workspaceIds[account];
    if (!workspaceId) {
      await route.continue();
      return;
    }
    const project = privateAccountProject(workspaceId, account);
    const conversation = privateAccountConversation(workspaceId, account);
    if (url.pathname === `/api/workspaces/${workspaceId}/conversations`) return jsonResponse(route, [conversation]);
    if (url.pathname === `/api/conversations/${conversation.id}`) return jsonResponse(route, conversation);
    if (url.pathname === `/api/conversations/${conversation.id}/messages`) return jsonResponse(route, privateAccountMessages(account));
    if (url.pathname === `/api/workspaces/${workspaceId}/projects`) {
      return jsonResponse(route, url.searchParams.get("status") === "Archived" ? [] : [project]);
    }
    if (url.pathname === `/api/projects/${project.id}`) return jsonResponse(route, project);
    if (url.pathname === `/api/projects/${project.id}/overview`) {
      return jsonResponse(route, {
        project,
        workspace: { id: workspaceId, name: `Account ${account} synthetic workspace`, slug: workspaceId, type: "Personal", role: "Owner" },
        counts: { files: 1, assets: 1, conversations: 1, activity: 0 },
        conversations: [conversation],
        recentActivity: [],
      });
    }
    if (url.pathname === `/api/workspaces/${workspaceId}/files`) return jsonResponse(route, [privateAccountFile(workspaceId, account)]);
    if (url.pathname === `/api/workspaces/${workspaceId}/assets` || url.pathname === "/api/assets") {
      const asset = privateAccountAsset(workspaceId, studio.assetType, account);
      return jsonResponse(route, { items: [asset], page: 1, pageSize: 6, totalCount: 1, totalPages: 1 });
    }
    await route.continue();
  });
}

async function createSession(page: Page, request: import("@playwright/test").APIRequestContext, testUser: TestUser) {
  assertLoopback(webUrl, "E2E_WEB_URL");
  assertLoopback(E2E_API_URL, "E2E_API_URL");
  if (syntheticSessionMode) {
    const token = `synthetic-${randomUUID()}`;
    const response = await request.post(`${E2E_API_URL}/__test__/session`, {
      data: { token, displayName: testUser.displayName, email: testUser.email, password: testUser.password, accountKey: "A" },
    });
    expect(response.status()).toBe(201);
    await page.context().addCookies([{ name: "taslim.auth", value: token, url: webUrl, httpOnly: true, sameSite: "Lax" }]);
    return token;
  }
  await registerInUi(page, testUser, true);
  const cookie = (await page.context().cookies(webUrl)).find(item => item.name === "taslim.auth");
  if (!cookie) throw new Error("The local E2E registration did not issue taslim.auth.");
  return cookie.value;
}

async function seedSyntheticAccount(request: import("@playwright/test").APIRequestContext, user: TestUser, account: TestAccount) {
  const token = `synthetic-${account.toLowerCase()}-${randomUUID()}`;
  const response = await request.post(`${E2E_API_URL}/__test__/session`, {
    data: { token, displayName: user.displayName, email: user.email, password: user.password, accountKey: account },
  });
  expect(response.status()).toBe(201);
  return token;
}

async function targetControl(page: Page, studio: StudioCase): Promise<Locator> {
  if (studio.navKind === "chat-button") return page.getByRole("button", { name: new RegExp(`PRIVATE_CHAT_SENTINEL_${stamp}`) }).first();
  return page.locator(`a[href="${studio.path}"]`).first();
}

function makeMonitor(): Monitor {
  return { rows: [], byRequest: new Map(), externalAttempts: [], generationPosts: [], blockedGenerationPosts: [], revokedAt: 0 };
}

async function markerText(page: Page) {
  return page.locator("body").innerText().catch(() => "");
}

async function noPrivateMarkerVisible(page: Page, studio: StudioCase) {
  const text = await markerText(page);
  return studio.markers.filter(marker => text.includes(marker));
}

async function visibleAccountMarkers(page: Page, account: TestAccount, studio: StudioCase) {
  const text = await markerText(page);
  return accountMarkers(account, studio).filter(marker => text.includes(marker));
}

async function accountTargetControl(page: Page, studio: StudioCase, account: TestAccount): Promise<Locator> {
  if (studio.navKind === "chat-button") {
    return page.getByRole("button", { name: new RegExp(accountStudioMarker(account, studio)) }).first();
  }
  return page.locator(`a[href="${accountStudioPath(account, studio)}"]`).first();
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
      monitor.revokedAt = performance.now();

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

const chargeableChatPostPaths = [
  `/api/conversations/${conversationId}/messages`,
  `/api/conversations/${conversationId}/messages/stream`,
  `/api/conversations/${conversationId}/messages/message-private-guard-probe/regenerate`,
];

test("exact Chat chargeable POST paths are observed and blocked before API/provider dispatch", async ({ browser, request }) => {
  test.setTimeout(60_000);
  assertLoopback(webUrl, "E2E_WEB_URL");
  assertLoopback(E2E_API_URL, "E2E_API_URL");
  const context = await browser.newContext({ baseURL: webUrl, ignoreHTTPSErrors: syntheticSessionMode, viewport: { width: 1365, height: 900 } });
  const monitor = makeMonitor();
  let providerPostsBefore: number | undefined;
  try {
    if (syntheticSessionMode) {
      const before = await request.get(`${E2E_API_URL}/__test__/summary`);
      expect(before.status()).toBe(200);
      providerPostsBefore = (await before.json() as { generationPosts: number }).generationPosts;
    }
    await installAccountSwitchNetworkGuard(context, monitor, studios[0], () => "loggedOut", { A: "", B: "" });
    const page = await context.newPage();
    observePage(page, monitor);
    await page.goto("/");

    for (const path of chargeableChatPostPaths) expect(isGenerationPost("POST", path), `${path} must be classified as chargeable Chat dispatch`).toBe(true);
    const privateReadPaths = [
      "/api/workspaces",
      "/api/projects/project-private-route-probe",
      "/api/projects/project-private-route-probe/overview",
      "/api/generation/jobs",
      "/api/generation/jobs/job-private-route-probe",
      "/api/research-generation/jobs/job-private-route-probe/sources",
      "/api/research-generation/jobs/job-private-route-probe/sources/export",
      "/api/assets/asset-private-route-probe/representations/representation-private-route-probe/download",
    ];
    for (const path of privateReadPaths) expect(isProtectedRead("GET", path), `${path} must be classified as a private read`).toBe(true);
    expect(isProtectedRead("POST", "/api/generation/jobs")).toBe(false);

    const probeResults = await Promise.all(chargeableChatPostPaths.map(path => page.evaluate(async ({ requestPath }) => {
      try {
        await fetch(requestPath, {
          method: "POST",
          credentials: "same-origin",
          headers: { "Content-Type": "text/plain" },
          body: "synthetic route-guard probe only",
        });
        return "unexpected-response";
      } catch {
        return "blocked-before-dispatch";
      }
    }, { requestPath: path })));
    expect(probeResults).toEqual(chargeableChatPostPaths.map(() => "blocked-before-dispatch"));
    expect([...new Set(monitor.blockedGenerationPosts)].sort()).toEqual([...chargeableChatPostPaths].sort());
    expect([...new Set(monitor.generationPosts)].sort()).toEqual([...chargeableChatPostPaths].sort());
    expect(monitor.externalAttempts).toEqual([]);

    let providerPostsAfter: number | undefined;
    if (syntheticSessionMode) {
      const after = await request.get(`${E2E_API_URL}/__test__/summary`);
      expect(after.status()).toBe(200);
      providerPostsAfter = (await after.json() as { generationPosts: number }).generationPosts;
      expect(providerPostsAfter).toBe(providerPostsBefore);
    }
    console.log(`STUDIOS_CHAT_CHARGEABLE_GUARD_EVIDENCE ${JSON.stringify({ paths: chargeableChatPostPaths, browserAttemptsBlocked: [...new Set(monitor.blockedGenerationPosts)].sort(), syntheticProviderDispatchCountBefore: providerPostsBefore, syntheticProviderDispatchCountAfter: providerPostsAfter, blockedExternalAttempts: monitor.externalAttempts })}`);
  } finally {
    await context.close();
  }
});

for (const studio of studios) {
  test(`${studio.name}: account A logout and account B login isolate both tabs and history`, async ({ browser, request, testUser }) => {
    test.setTimeout(180_000);
    assertLoopback(webUrl, "E2E_WEB_URL");
    assertLoopback(E2E_API_URL, "E2E_API_URL");
    const accountBUser: TestUser = {
      displayName: "Synthetic Account B",
      email: `e2e+account-b-${randomUUID()}@example.test`,
      password: testUser.password,
    };
    if (syntheticSessionMode) {
      await seedSyntheticAccount(request, accountBUser, "B");
    } else {
      const setupContext = await browser.newContext({ baseURL: webUrl, viewport: { width: 1365, height: 900 } });
      const setupMonitor = makeMonitor();
      try {
        await installAccountSwitchNetworkGuard(setupContext, setupMonitor, studio, () => "loggedOut", { A: "", B: "" });
        const setupPage = await setupContext.newPage();
        await registerInUi(setupPage, accountBUser, false);
        await apiJson(setupPage.request, "POST", "/api/auth/logout");
        expect(setupMonitor.generationPosts).toEqual([]);
        expect(setupMonitor.externalAttempts).toEqual([]);
      } finally {
        await setupContext.close();
      }
    }

    const context = await browser.newContext({ baseURL: webUrl, ignoreHTTPSErrors: syntheticSessionMode, viewport: { width: 1365, height: 900 } });
    const monitor = makeMonitor();
    const workspaceIds: Record<TestAccount, string> = { A: "", B: "" };
    let activeAccount: AccountState = "A";
    let logoutSignalAt = 0;
    let loginSignalAt = 0;
    let providerPostsBefore: number | undefined;
    const pathA = accountStudioPath("A", studio);
    const pathB = accountStudioPath("B", studio);
    const aMarkers = accountMarkers("A", studio);
    const bMarker = accountStudioMarker("B", studio);
    try {
      if (syntheticSessionMode) {
        const before = await request.get(`${E2E_API_URL}/__test__/summary`);
        expect(before.status()).toBe(200);
        providerPostsBefore = (await before.json() as { generationPosts: number }).generationPosts;
      }
      await installAccountSwitchNetworkGuard(context, monitor, studio, () => activeAccount, workspaceIds);
      if (syntheticSessionMode) {
        await context.route("**/api/projects/project-private-account-a**", async route => {
          const request = route.request();
          if (request.method() !== "GET") return route.continue();
          if (activeAccount !== "A" || !workspaceIds.A) {
            return route.fulfill({
              status: 403,
              contentType: "application/json; charset=utf-8",
              headers: { "cache-control": "no-store, private" },
              body: JSON.stringify({ error: { code: "PROJECT_ACCESS_REVOKED" } }),
            });
          }
          const project = privateAccountProject(workspaceIds.A, "A");
          const url = new URL(request.url());
          if (url.pathname.endsWith("/overview")) {
            return jsonResponse(route, {
              project,
              workspace: { id: workspaceIds.A, name: "Synthetic Account A Workspace", slug: workspaceIds.A, type: "Personal", role: "Owner" },
              counts: { files: 1, assets: 1, conversations: 1, activity: 0 },
              conversations: [privateAccountConversation(workspaceIds.A, "A")],
              recentActivity: [],
            });
          }
          return jsonResponse(route, project);
        });
      }
      const pageA = await context.newPage();
      const pageB = await context.newPage();
      observePage(pageA, monitor, () => activeAccount);
      observePage(pageB, monitor, () => activeAccount);

      await createSession(pageA, request, testUser);
      const authA = await apiJson<{ user: { email: string }; personalWorkspace: { id: string } }>(pageA.request, "GET", "/api/auth/me");
      workspaceIds.A = authA.personalWorkspace.id;
      expect(authA.user.email).toBe(testUser.email);

      const accountAResponse = await pageA.goto(pathA);
      expect(accountAResponse?.status()).toBe(200);
      await expect.poll(async () => (await markerText(pageA)).includes(accountStudioMarker("A", studio)), { timeout: 15_000 }).toBe(true);
      if (syntheticSessionMode) {
        const projectReadPaths = [
          `/api/projects/${accountProjectIds.A}`,
          `/api/projects/${accountProjectIds.A}/overview`,
        ];
        const projectReadResults = await Promise.all(projectReadPaths.map(routePath => pageA.evaluate(async ({ path }) => {
          const response = await fetch(path, { credentials: "same-origin" });
          return { status: response.status, body: await response.text() };
        }, { path: routePath })));
        expect(projectReadResults.map(result => result.status)).toEqual([200, 200]);
        for (const result of projectReadResults) expect(result.body).toContain(accountMarker("A", "PROJECT"));
        const observedProjectReadPaths = monitor.rows
          .filter(row => row.method === "GET" && projectReadPaths.includes(row.path))
          .map(row => row.path)
          .sort();
        expect(observedProjectReadPaths).toEqual([...projectReadPaths].sort());
      }

      await pageB.goto(studio.sourcePath);
      const aTarget = await accountTargetControl(pageB, studio, "A");
      await expect(aTarget).toBeVisible();
      let preLogoutPrefetches = 0;
      if (studio.navKind === "link") {
        await aTarget.hover();
        await pageB.waitForTimeout(900);
        preLogoutPrefetches = monitor.rows.filter(row => row.path === pathA && row.rsc && row.prefetch && row.accountAtRequest === "A").length;
        if (requireProductionPrefetch) expect(preLogoutPrefetches).toBeGreaterThan(0);
      } else {
        test.info().annotations.push({ type: "navigation-shape", description: "Chat uses a client button/router transition for the conversation route; the positive native-Link prefetch control applies to Document, Image, and Presentation." });
      }
      await aTarget.click();
      await expect.poll(async () => (await markerText(pageB)).includes(accountStudioMarker("A", studio)), { timeout: 15_000 }).toBe(true);

      await pageA.locator('a[href="/account"]').first().click();
      await expect(pageA).toHaveURL(/\/account(?:\?|$)/);
      const logoutResponsePromise = pageA.waitForResponse(response => {
        const url = new URL(response.url());
        const isLogoutResponse = response.request().method() === "POST" && url.pathname === "/api/auth/logout";
        if (isLogoutResponse) {
          logoutSignalAt = performance.now();
          activeAccount = "loggedOut";
        }
        return isLogoutResponse;
      });
      await pageA.locator("button.security-logout").click();
      const logoutResponse = await logoutResponsePromise;
      expect(logoutResponse.status()).toBe(200);
      expect(logoutSignalAt).toBeGreaterThan(0);
      await expect(pageA).toHaveURL(/\/login(?:\?.*)?$/);

      const tabAMarkersAtLogout = await visibleAccountMarkers(pageA, "A", studio);
      const tabBMarkersAtLogout = await visibleAccountMarkers(pageB, "A", studio);
      expect.soft(tabAMarkersAtLogout, `${studio.name} Account A content remained in tab A after observed logout`).toEqual([]);
      expect.soft(tabBMarkersAtLogout, `${studio.name} Account A content remained in tab B after observed logout`).toEqual([]);

      const loginResponsePromise = pageA.waitForResponse(response => {
        const url = new URL(response.url());
        const isLoginResponse = response.request().method() === "POST" && url.pathname === "/api/auth/login";
        if (isLoginResponse) {
          loginSignalAt = performance.now();
          activeAccount = "B";
        }
        return isLoginResponse;
      });
      await loginInUi(pageA, accountBUser);
      const loginResponse = await loginResponsePromise;
      expect(loginResponse.status()).toBe(200);
      expect(loginSignalAt).toBeGreaterThan(logoutSignalAt);
      const authB = await apiJson<{ user: { email: string }; personalWorkspace: { id: string } }>(pageA.request, "GET", "/api/auth/me");
      workspaceIds.B = authB.personalWorkspace.id;
      expect(authB.user.email).toBe(accountBUser.email);
      expect(workspaceIds.B).not.toBe(workspaceIds.A);

      const tabAMarkersAfterBLogin = await visibleAccountMarkers(pageA, "A", studio);
      const tabBMarkersAfterBLogin = await visibleAccountMarkers(pageB, "A", studio);
      expect.soft(tabAMarkersAfterBLogin, `${studio.name} Account A content appeared in Account B tab A after login`).toEqual([]);
      expect.soft(tabBMarkersAfterBLogin, `${studio.name} Account A content remained visible in the second tab after Account B login`).toEqual([]);

      const historyLeakAudit: Array<{ tab: string; step: string; aMarkers: string[] }> = [];
      const readCapturedBodiesSince = async (rowStart: number) => {
        const bodies = monitor.rows.slice(rowStart).filter(row => row.body).map(row => row.body!);
        await Promise.all(bodies);
      };
      const assertNoAccountA = async (tab: string, page: Page, step: string) => {
        const markers = await visibleAccountMarkers(page, "A", studio);
        historyLeakAudit.push({ tab, step, aMarkers: markers });
        expect.soft(markers, `${studio.name} Account A marker visible in ${tab} at ${step} after Account B login`).toEqual([]);
      };

      const accountBRequestStart = monitor.rows.length;
      const accountBResponse = await pageA.goto(pathB);
      expect(accountBResponse?.status()).toBe(200);
      await expect.poll(async () => (await markerText(pageA)).includes(bMarker), { timeout: 15_000 }).toBe(true);
      await readCapturedBodiesSince(accountBRequestStart);
      await assertNoAccountA("tab A", pageA, "Account B Studio load");

      const historyNavigationAudit: Array<{ tab: string; step: string; before: string; after: string; completed: boolean; error?: string }> = [];
      const revisitHistory = async (tab: string, page: Page, step: "back" | "forward" | "reload", navigate: () => Promise<unknown>) => {
        const before = page.url();
        const rowStart = monitor.rows.length;
        let error: string | undefined;
        try {
          await navigate();
        } catch (caught) {
          error = caught instanceof Error ? caught.message : String(caught);
        }
        await page.waitForTimeout(150);
        await readCapturedBodiesSince(rowStart);
        const after = page.url();
        const completed = !error && (step === "reload" || before !== after);
        historyNavigationAudit.push({ tab, step, before, after, completed, error });
        expect.soft(completed, `${studio.name} ${tab} ${step} history navigation must complete`).toBe(true);
        await assertNoAccountA(tab, page, step);
      };

      await revisitHistory("tab B", pageB, "back", () => pageB.goBack({ waitUntil: "commit", timeout: 8_000 }));
      await revisitHistory("tab B", pageB, "forward", () => pageB.goForward({ waitUntil: "commit", timeout: 8_000 }));
      await revisitHistory("tab B", pageB, "reload", () => pageB.reload({ waitUntil: "commit", timeout: 8_000 }));
      await pageB.goto(pathB);
      await expect.poll(async () => (await markerText(pageB)).includes(bMarker), { timeout: 15_000 }).toBe(true);
      await assertNoAccountA("tab B", pageB, "Account B Studio load");

      await revisitHistory("tab A", pageA, "back", () => pageA.goBack({ waitUntil: "commit", timeout: 8_000 }));
      await revisitHistory("tab A", pageA, "forward", () => pageA.goForward({ waitUntil: "commit", timeout: 8_000 }));
      await revisitHistory("tab A", pageA, "reload", () => pageA.reload({ waitUntil: "commit", timeout: 8_000 }));

      await pageA.waitForTimeout(300);
      await pageB.waitForTimeout(300);
      const afterLogoutRows = monitor.rows.filter(row => row.time >= logoutSignalAt);
      const targetsAccountA = (row: RequestRow) => {
        const url = new URL(row.url);
        const pathSegments = url.pathname.split("/").map(segment => decodeURIComponent(segment));
        const aIds = [workspaceIds.A, accountProjectIds.A, accountConversationIds.A].filter(Boolean);
        return aIds.some(id => pathSegments.includes(id) || url.searchParams.get("workspaceId") === id);
      };
      const aProtectedGetAttempts = afterLogoutRows.filter(row => isProtectedRead(row.method, row.path)
        && (targetsAccountA(row) || row.accountAtRequest === "A" || row.accountAtRequest === "loggedOut"));
      const deniedAProtectedGets = aProtectedGetAttempts.filter(row => row.status === 401 || row.status === 403);
      const nonDeniedAProtectedGets = aProtectedGetAttempts.filter(row => row.status !== undefined && row.status !== 401 && row.status !== 403);
      const unresolvedAProtectedGets = aProtectedGetAttempts.filter(row => row.status === undefined);
      const aFlightPrefetches = afterLogoutRows.filter(row => row.rsc && row.prefetch && (
        targetsAccountA(row)
        || (row.accountAtRequest === "loggedOut" && row.path === pathA)
      ));
      const capturedResponseBodies = await Promise.all(afterLogoutRows.filter(row => row.body).map(async row => ({
        row,
        body: await row.body!,
      })));
      const privateResponseLeaks = capturedResponseBodies.flatMap(({ row, body }) => {
        const markers = aMarkers.filter(marker => body.includes(marker));
        return markers.length > 0 ? [{ path: row.path, status: row.status, markers }] : [];
      });
      const privateResponseBodyReadFailures = capturedResponseBodies
        .filter(({ row }) => row.bodyReadStatus !== "read")
        .map(({ row }) => ({ path: row.path, status: row.status, bodyReadStatus: row.bodyReadStatus, error: row.bodyReadError }));
      const chargeablePostsAfterLogout = afterLogoutRows.filter(row => isGenerationPost(row.method, row.path)).map(row => ({ path: row.path, time: row.time }));
      let providerPostsAfter: number | undefined;
      if (syntheticSessionMode) {
        const summary = await request.get(`${E2E_API_URL}/__test__/summary`);
        expect(summary.status()).toBe(200);
        providerPostsAfter = (await summary.json() as { generationPosts: number }).generationPosts;
        expect.soft(providerPostsAfter).toBe(providerPostsBefore);
      }
      const evidence = {
        studio: studio.name,
        syntheticLocalAuthAndApi: syntheticSessionMode,
        accountAWorkspaceId: workspaceIds.A,
        accountBWorkspaceId: workspaceIds.B,
        initialAccountAStatus: accountAResponse?.status(),
        accountBStatus: accountBResponse?.status(),
        accountAMarkerBeforeLogout: accountStudioMarker("A", studio),
        accountBMarkerAfterLogin: bMarker,
        preLogoutPositiveNativeLinkPrefetches: preLogoutPrefetches,
        logoutResponseStatus: logoutResponse.status(),
        logoutObservedAt: logoutSignalAt,
        accountBLoginStatus: loginResponse.status(),
        accountBLoginObservedAt: loginSignalAt,
        accountAMarkersAtLogout: { tabA: tabAMarkersAtLogout, tabB: tabBMarkersAtLogout },
        accountAMarkersAfterBLogin: { tabA: tabAMarkersAfterBLogin, tabB: tabBMarkersAfterBLogin },
        accountBMarkerVisible: { tabA: (await markerText(pageA)).includes(bMarker), tabB: (await markerText(pageB)).includes(bMarker) },
        historyLeakAudit,
        historyNavigationAudit,
        aProtectedGetAttemptsAfterLogout: aProtectedGetAttempts.map(row => ({ path: row.path, status: row.status, accountAtRequest: row.accountAtRequest, failure: row.failure })),
        correctlyDeniedAProtectedGetResponses: deniedAProtectedGets.length,
        nonDeniedAProtectedGetResponses: nonDeniedAProtectedGets.map(row => ({ path: row.path, status: row.status })),
        unresolvedAProtectedGetAttempts: unresolvedAProtectedGets.map(row => ({ path: row.path, failure: row.failure })),
        strictAFlightPrefetchAttemptsAfterLogout: aFlightPrefetches.map(row => ({ path: row.path, status: row.status, accountAtRequest: row.accountAtRequest })),
        privateAResponseLeaks: privateResponseLeaks,
        privateResponseBodyReadFailures,
        chargeableChatOrProviderPostsAfterLogout: chargeablePostsAfterLogout,
        allChargeableChatOrProviderPosts: monitor.generationPosts,
        blockedChargeablePosts: monitor.blockedGenerationPosts,
        syntheticProviderDispatchCountBefore: providerPostsBefore,
        syntheticProviderDispatchCountAfter: providerPostsAfter,
        blockedExternalAttempts: monitor.externalAttempts,
      };
      console.log(`STUDIOS_ACCOUNT_SWITCH_EVIDENCE ${JSON.stringify(evidence)}`);

      expect.soft(privateResponseLeaks, "Account A private content must not appear in any API or Flight response after logout").toEqual([]);
      expect.soft(privateResponseBodyReadFailures, "Every post-logout API/Flight response body must be fully readable before claiming no private marker leak").toEqual([]);
      expect.soft(nonDeniedAProtectedGets.map(row => ({ path: row.path, status: row.status })), "Any post-logout Account A protected GET reaching the API must be denied with 401/403").toEqual([]);
      expect.soft(unresolvedAProtectedGets.map(row => row.path), "Every observed Account A protected GET attempt must have an observable response").toEqual([]);
      expect.soft(historyNavigationAudit.every(row => row.completed), "Back, forward, and reload must complete in both tabs after Account B login").toBe(true);
      expect.soft(aProtectedGetAttempts.map(row => ({ path: row.path, status: row.status })), "Strict contract: zero Account A protected GET attempts after observed logout, separate from correct 401/403 denial").toEqual([]);
      expect.soft(aFlightPrefetches.map(row => ({ path: row.path, status: row.status })), "Strict contract: zero Account A Flight/Link prefetches after observed logout/account switch").toEqual([]);
      expect.soft(monitor.generationPosts, "No chargeable Chat or provider POST is allowed in the account-switch flow").toEqual([]);
      expect.soft(monitor.externalAttempts, "All browser egress outside loopback is blocked and should remain unused").toEqual([]);
      expect.soft(monitor.blockedGenerationPosts, "The account-switch flow must not trigger a blocked chargeable/provider POST").toEqual([]);
    } finally {
      await context.close();
    }
  });
}

for (const studio of studios) {
  test(`${studio.name}: explicit same-tab logout clears private state and stops post-signal protected requests`, async ({ browser, request, testUser }) => {
    test.setTimeout(90_000);
    assertLoopback(webUrl, "E2E_WEB_URL");
    assertLoopback(E2E_API_URL, "E2E_API_URL");
    const context = await browser.newContext({ baseURL: webUrl, ignoreHTTPSErrors: syntheticSessionMode, viewport: { width: 1365, height: 900 } });
    const monitor = makeMonitor();
    let revoked = false;
    let logoutSignalAt = 0;
    try {
      await addExternalEgressBlock(context, monitor);
      const page = await context.newPage();
      const token = await createSession(page, request, testUser);
      const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
      await installLocalOnlyRoutes(context, monitor, studio, auth.personalWorkspace.id, () => revoked);
      observePage(page, monitor);

      const authorizedResponse = await page.goto(studio.path);
      expect(authorizedResponse?.status()).toBe(200);
      await expect.poll(async () => (await markerText(page)).includes(studio.markers[0]), { timeout: 15_000 }).toBe(true);
      await page.locator('a[href="/account"]').first().click();
      await expect(page).toHaveURL(/\/account(?:\?|$)/);
      const logoutResponsePromise = page.waitForResponse(response => {
        const url = new URL(response.url());
        const isLogoutResponse = response.request().method() === "POST" && url.pathname === "/api/auth/logout";
        if (isLogoutResponse) {
          logoutSignalAt = performance.now();
          revoked = true;
        }
        return isLogoutResponse;
      });
      await page.locator("button.security-logout").click();
      const logoutResponse = await logoutResponsePromise;
      expect(logoutResponse.status()).toBe(200);
      await expect(page).toHaveURL(/\/login(?:\?.*)?$/);

      // This is the positive, same-tab logout signal. The old out-of-band
      // revocation cases above remain independent and intentionally strict.
      const signalAt = logoutSignalAt;
      expect(signalAt).toBeGreaterThan(0);
      await page.waitForTimeout(500);
      const afterSignalRows = monitor.rows.filter(row => row.time >= signalAt);
      const postSignalProtectedGets = afterSignalRows.filter(row => isProtectedRead(row.method, row.path));
      const postSignalTargetPrefetches = afterSignalRows.filter(row => row.path === studio.path && row.rsc && row.prefetch);
      const privateResponseBodies: Array<{ path: string; status?: number }> = [];
      for (const row of afterSignalRows) {
        if (!row.body) continue;
        const body = await row.body;
        if (privateMarkers.some(marker => body.includes(marker))) privateResponseBodies.push({ path: row.path, status: row.status });
      }
      const visibleMarkers = await noPrivateMarkerVisible(page, studio);
      console.log(`STUDIOS_EXPLICIT_LOGOUT_EVIDENCE ${JSON.stringify({ studio: studio.name, syntheticLocalAuth: syntheticSessionMode, logoutStatus: logoutResponse.status(), postSignalProtectedGets: postSignalProtectedGets.map(row => ({ path: row.path, status: row.status })), postSignalTargetPrefetches: postSignalTargetPrefetches.map(row => ({ path: row.path, status: row.status })), privateResponseBodies, visibleMarkers, generationPosts: monitor.generationPosts, blockedExternalAttempts: monitor.externalAttempts })}`);

      expect(visibleMarkers, `${studio.name} private marker remained visible after explicit same-tab logout completed`).toEqual([]);
      expect(postSignalProtectedGets.map(row => ({ path: row.path, status: row.status })), `${studio.name} issued a protected GET after the explicit logout signal`).toEqual([]);
      expect(postSignalTargetPrefetches.map(row => ({ path: row.path, status: row.status })), `${studio.name} prefetched its private route after the explicit logout signal`).toEqual([]);
      expect(privateResponseBodies, `${studio.name} served private data in a response after the explicit logout signal`).toEqual([]);
      expect(monitor.generationPosts, "No generation/provider POST is allowed in this auth regression.").toEqual([]);
      expect(monitor.externalAttempts, "All browser egress outside loopback is blocked and should remain unused.").toEqual([]);
      expect(token).toBeTruthy();
    } finally {
      await context.close();
    }
  });
}
