import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// Keyboard-only modal regression for the projects create/edit dialog. The
// suite pins the three behaviors the shared dialog hook owns: initial focus
// on the named field, focus containment through Tab/Shift+Tab wrap-around,
// Escape-to-close, and restoration of focus to the exact opener control.
// All API traffic is intercepted: only synthetic GET responses are served,
// every write is recorded as a failure, and any unmapped GET path fails the
// test so silent endpoint drift cannot widen this spec's footprint.

const WORKSPACE_ID = "11111111-1111-4111-8111-111111111111";
const PROJECT_ID = "22222222-2222-4222-8222-222222222222";

const sessionPayload = {
  user: {
    id: "33333333-3333-4333-8333-333333333333",
    email: "modal-keyboard-e2e@example.test",
    displayName: "Modal Keyboard E2E",
    preferredLanguage: "en",
    personalWorkspaceId: WORKSPACE_ID,
    createdAt: "2026-11-01T00:00:00Z",
    defaultGenerationLanguage: "en",
    timeZone: "UTC",
    outputPreference: "balanced",
    includeSourceLinks: false,
    onboardingCompletedAt: "2026-11-01T00:00:00Z",
    onboardingIntent: "project",
    isAdmin: false,
  },
  personalWorkspace: {
    id: WORKSPACE_ID,
    name: "Modal Keyboard Workspace",
    slug: "modal-keyboard-workspace",
    type: "Personal",
    role: "Owner",
  },
};

const projectPayload = {
  id: PROJECT_ID,
  workspaceId: WORKSPACE_ID,
  name: "Keyboard Focus Project",
  description: null,
  instructions: null,
  contextNotes: null,
  type: "Business",
  status: "Active",
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: "2026-11-01T00:00:00Z",
  archivedAt: null,
};

function json(body: unknown) {
  return { status: 200 as const, contentType: "application/json", body: JSON.stringify(body) };
}

async function installReadOnlyApiMock(page: Page) {
  const unmapped: string[] = [];
  const writes: string[] = [];
  await page.route(/\/api\//, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method().toUpperCase();
    if (method !== "GET" && method !== "HEAD") {
      writes.push(`${method} ${url.pathname}`);
      return route.fulfill({ status: 405, contentType: "application/json", body: "{}" });
    }
    const path = url.pathname;
    if (path.endsWith("/api/auth/me")) return route.fulfill(json(sessionPayload));
    if (path.includes("/projects")) return route.fulfill(json([projectPayload]));
    if (path === "/api/workspaces" || /^\/api\/workspaces\/[^/]+$/.test(path)) return route.fulfill(json([sessionPayload.personalWorkspace]));
    if (path.endsWith("/api/auth/csrf")) return route.fulfill(json({ token: "synthetic-csrf" }));
    // App-shell chrome pollers (activity bell / notifications) run independently
    // of the dialog flow; serve their documented { unreadCount } shape.
    if (path.endsWith("/api/notifications/unread-count") || path.endsWith("/api/activity/unread-count")) return route.fulfill(json({ unreadCount: 0 }));
    unmapped.push(`${method} ${path}${url.search}`);
    return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
  });
  return { unmapped, writes };
}

async function expectFocusInsideDialog(page: Page) {
  const inside = await page.evaluate(() => {
    const active = document.activeElement;
    return active !== null && active.closest('[role="dialog"]') !== null;
  });
  expect(inside, `focus escaped the dialog (active: ${await page.evaluate(() => document.activeElement?.tagName ?? "null")})`).toBe(true);
}

test("project modal keeps keyboard focus contained and returns it to the opener", async ({ authenticatedPage: page }) => {
  // `/projects` became a server-gated route on main (ea0dc6b9, PR #1237: `dynamic =
  // "force-dynamic"` + `await requireAuthenticatedPage("/projects")`). That session probe
  // runs inside the Next server process against the real API, so a browser-side
  // `page.route` mock cannot satisfy it and the SSR render answers 307 -> `/login`.
  // Authenticate through the shared fixture first, then layer the read-only synthetic
  // mock on top: the dialog content stays deterministic and every in-flow write or
  // unmapped read is still trapped by the assertions below.
  const api = await installReadOnlyApiMock(page);
  await page.goto("/projects");

  const newProjectButton = page.getByRole("button", { name: /^new project$/i }).first();
  await expect(newProjectButton).toBeVisible();

  // Opening with the keyboard moves focus to the named field inside a real dialog.
  await newProjectButton.focus();
  await page.keyboard.press("Enter");
  const createDialog = page.getByRole("dialog", { name: /^create a project$/i });
  await expect(createDialog).toBeVisible();
  await expect(createDialog.getByLabel(/^project name$/i)).toBeFocused();

  // Tab cycles stay contained, including the wrap from the last element.
  for (let press = 1; press <= 25; press += 1) {
    await page.keyboard.press("Tab");
    await expectFocusInsideDialog(page);
  }
  // Reverse direction wraps too.
  for (let press = 1; press <= 3; press += 1) {
    await page.keyboard.press("Shift+Tab");
    await expectFocusInsideDialog(page);
  }

  // Escape closes the dialog and focus lands back on the exact opener.
  await page.keyboard.press("Escape");
  await expect(createDialog).toBeHidden();
  await expect(newProjectButton).toBeFocused();

  // The per-card Edit opener restores focus to that card's button.
  const editButton = page.getByRole("button", { name: /^edit$/i }).first();
  await expect(editButton).toBeVisible();
  await editButton.focus();
  await page.keyboard.press("Enter");
  const editDialog = page.getByRole("dialog", { name: /^edit project$/i });
  await expect(editDialog).toBeVisible();
  await expect(editDialog.getByLabel(/^project name$/i)).toBeFocused();
  for (let press = 1; press <= 10; press += 1) {
    await page.keyboard.press("Tab");
    await expectFocusInsideDialog(page);
  }
  await page.keyboard.press("Escape");
  await expect(editDialog).toBeHidden();
  await expect(editButton).toBeFocused();

  expect(writesOf(api), "the modal flow must not reach any API write endpoint").toEqual([]);
  expect(api.unmapped, "unexpected API reads indicate endpoint drift").toEqual([]);
});

test("project edit restores focus to its opener while the save-triggered list reload is pending", async ({ authenticatedPage: page }) => {
  let currentProject = { ...projectPayload };
  let listReads = 0;
  let updated = false;
  let refreshHeld = false;
  const writes: { method: string; path: string; ifMatch: string | null }[] = [];
  const unmapped: string[] = [];
  let releaseRefresh!: () => void;
  const refreshGate = new Promise<void>((resolve) => { releaseRefresh = resolve; });
  let markRefreshStarted!: () => void;
  const refreshStarted = new Promise<void>((resolve) => { markRefreshStarted = resolve; });

  await page.route(/\/api\//, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method().toUpperCase();
    const path = url.pathname;
    if (method === "PATCH" && path === `/api/projects/${PROJECT_ID}`) {
      writes.push({ method, path, ifMatch: request.headers()["if-match"] ?? null });
      const input = JSON.parse(request.postData() ?? "{}") as Partial<typeof currentProject>;
      currentProject = { ...currentProject, ...input, updatedAt: "2026-11-25T00:00:00Z" };
      updated = true;
      return route.fulfill({ status: 200, contentType: "application/json", headers: { ETag: '"revision-2"', "Access-Control-Expose-Headers": "ETag" }, body: JSON.stringify(currentProject) });
    }
    if (method !== "GET" && method !== "HEAD") {
      writes.push({ method, path, ifMatch: request.headers()["if-match"] ?? null });
      return route.fulfill({ status: 405, contentType: "application/json", body: "{}" });
    }
    if (path.endsWith("/api/auth/me")) return route.fulfill(json(sessionPayload));
    if (path === `/api/workspaces/${WORKSPACE_ID}/projects`) {
      listReads += 1;
      if (updated && !refreshHeld) {
        refreshHeld = true;
        markRefreshStarted();
        await refreshGate;
      }
      return route.fulfill(json([currentProject]));
    }
    if (path === `/api/projects/${PROJECT_ID}`) {
      return route.fulfill({ status: 200, contentType: "application/json", headers: { ETag: '"revision-1"', "Access-Control-Expose-Headers": "ETag" }, body: JSON.stringify(currentProject) });
    }
    if (path === "/api/workspaces" || /^\/api\/workspaces\/[^/]+$/.test(path)) return route.fulfill(json([sessionPayload.personalWorkspace]));
    if (path.endsWith("/api/auth/csrf")) return route.fulfill(json({ token: "synthetic-csrf" }));
    if (path.endsWith("/api/notifications/unread-count") || path.endsWith("/api/activity/unread-count")) return route.fulfill(json({ unreadCount: 0 }));
    unmapped.push(`${method} ${path}${url.search}`);
    return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
  });

  await page.goto("/projects");
  const editButton = page.getByRole("button", { name: /^edit$/i }).first();
  await expect(editButton).toBeVisible();
  await editButton.focus();
  await page.keyboard.press("Enter");

  const editDialog = page.getByRole("dialog", { name: /^edit project$/i });
  await expect(editDialog).toBeVisible();
  await editDialog.getByLabel(/^project name$/i).fill("Saved Focus Project");
  await editDialog.getByRole("button", { name: /^save$/i }).click();
  await expect(editDialog).toBeHidden();
  try {
    await refreshStarted;
    // Keep the server list response pending: focus restoration must not depend
    // on the replacement list arriving after the edit row was cleared.
    await expect(editButton).toBeFocused();
  } finally {
    releaseRefresh();
  }

  await expect(page.getByText("Saved Focus Project", { exact: true })).toBeVisible();
  await expect(editButton).toBeFocused();
  expect(listReads).toBeGreaterThanOrEqual(2);
  expect(writes).toEqual([{ method: "PATCH", path: `/api/projects/${PROJECT_ID}`, ifMatch: '"revision-1"' }]);
  expect(unmapped, "unexpected API reads indicate endpoint drift").toEqual([]);
});

function writesOf(api: { writes: string[] }) {
  return api.writes;
}

test("a save that finishes after its edit dialog closes leaves the next dialog open", async ({ authenticatedPage: page }) => {
  const api = await installReadOnlyApiMock(page);
  let listReads = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === `/api/workspaces/${WORKSPACE_ID}/projects`) listReads += 1;
  });
  let releaseSave!: () => void;
  const pendingSave = new Promise<void>((resolve) => { releaseSave = resolve; });
  const projectPath = `/api/projects/${PROJECT_ID}`;
  await page.route(`**${projectPath}`, async (route) => {
    const method = route.request().method();
    if (method === "PATCH") await pendingSave;
    else if (method !== "GET") return route.fallback();
    return route.fulfill({
      ...json(projectPayload), headers: { ETag: '"revision-1"', "Access-Control-Expose-Headers": "ETag" },
    });
  });

  await page.goto("/projects");
  await page.getByRole("button", { name: /^edit$/i }).first().click();
  const edit = page.getByRole("dialog", { name: /^edit project$/i });
  await edit.getByLabel(/^project name$/i).fill("Updated project");
  const isSave = (request: import("@playwright/test").Request) =>
    request.method() === "PATCH" && new URL(request.url()).pathname === projectPath;
  const saveStarted = page.waitForRequest(isSave);
  await edit.getByRole("button", { name: /^save$/i }).click();
  await saveStarted;
  const readsBeforeClosing = listReads;
  const response = page.waitForResponse((item) => isSave(item.request()));
  try {
    await page.keyboard.press("Escape");
    await expect(edit).toBeHidden();
    await page.getByRole("button", { name: /^new project$/i }).first().click();
    await page.getByRole("dialog").getByLabel(/^project name$/i).fill("New draft");
  } finally {
    releaseSave();
  }
  await (await response).finished();
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  await expect(page.getByRole("dialog").getByLabel(/^project name$/i)).toHaveValue("New draft");
  expect(listReads).toBe(readsBeforeClosing);
  expect(api.writes).toEqual([]);
});
