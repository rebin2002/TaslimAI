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

// The projects route gained a server-side session gate in main `23f29d86`
// (PR #1247): `requireAuthenticatedPage("/projects")` validates the
// `taslim.auth` cookie against the API from inside the render, which a
// browser-level route mock cannot satisfy. Without a session the server answers
// with a redirect to `/login` and no dialog ever mounts. This spec therefore
// registers a genuine session through the fixture first, then installs the
// read-only mock so every component fetch stays synthetic; the write and
// unmapped-read tripwires at the end are unchanged.
test("project modal keeps keyboard focus contained and returns it to the opener", async ({ authenticatedPage: page }) => {
  const api = await installReadOnlyApiMock(page);
  await page.goto("/projects");
  // Fail loudly if the server gate ever redirects again: the tripwires below
  // would otherwise report an empty mock instead of the real cause.
  expect(page.url()).toContain("/projects");

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

function writesOf(api: { writes: string[] }) {
  return api.writes;
}
