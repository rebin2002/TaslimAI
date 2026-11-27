import { test, expect } from "./fixtures";

test.describe("workspace navigation and protected views", () => {
  test("searches workspace content and opens Activity and Notifications", async ({ authenticatedPage: page }) => {
    await page.goto("/search");
    await expect(page.getByRole("heading", { name: /search/i }).first()).toBeVisible();
    await page.getByRole("textbox", { name: "Search" }).fill("E2E no-match");
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await expect(page).toHaveURL(/\/search\?q=E2E%20no-match/);
    await expect(page.getByText(/no matches found/i)).toBeVisible();

    await page.goto("/notifications");
    await expect(page.getByRole("heading", { name: "Notifications", exact: true })).toBeVisible();
    const markAllRead = page.getByRole("button", { name: /mark all as read|mark all read/i });
    await expect(markAllRead).toBeVisible();
    await expect(markAllRead).toBeDisabled();
    await page.goto("/activity");
    await expect(page.getByRole("heading", { name: /^activity center$/i })).toBeVisible();
  });

  test("supports notification unread-to-read behavior with deterministic browser data", async ({ authenticatedPage: page }) => {
    const notification = {
      id: "00000000-0000-0000-0000-000000000001",
      workspaceId: "00000000-0000-0000-0000-000000000002",
      projectId: null,
      generationJobId: null,
      assetId: null,
      type: "generation.completed",
      resourceTitle: "E2E deterministic notification",
      createdAt: new Date().toISOString(),
      readAt: null,
      isRead: false,
      destination: "/activity",
    };
    let unread = true;
    await page.route("**/api/notifications?*", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ items: [{ ...notification, isRead: !unread, readAt: unread ? null : new Date().toISOString() }], page: 1, pageSize: 50, totalCount: 1, totalPages: 1, unreadCount: unread ? 1 : 0 }),
      });
    });
    await page.route("**/api/notifications/*/read", async (route) => {
      unread = false;
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ read: true }) });
    });
    await page.goto("/notifications");
    await expect(page.getByText("E2E deterministic notification")).toBeVisible();
    await page.getByRole("button", { name: /mark as read/i }).click();
    await expect(page.locator(".notification-new-label")).toHaveCount(0);
  });

  test("opens the notification popover from the keyboard and restores focus on Escape", async ({ authenticatedPage: page }) => {
    const notification = {
      id: "00000000-0000-0000-0000-000000000003",
      workspaceId: "00000000-0000-0000-0000-000000000004",
      projectId: null,
      generationJobId: null,
      assetId: null,
      type: "generation.completed",
      resourceTitle: "E2E keyboard notification",
      createdAt: new Date().toISOString(),
      readAt: null,
      isRead: false,
      destination: "/activity",
    };
    let unread = true;
    await page.route("**/api/notifications/unread-count*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ unreadCount: unread ? 1 : 0 }) });
    });
    await page.route("**/api/notifications?*", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ items: [{ ...notification, isRead: !unread, readAt: unread ? null : new Date().toISOString() }], page: 1, pageSize: 6, totalCount: 1, totalPages: 1, unreadCount: unread ? 1 : 0 }),
      });
    });
    await page.route("**/api/notifications/*/read", async (route) => {
      unread = false;
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ read: true }) });
    });
    await page.goto("/projects");

    const bell = page.getByRole("button", { name: /notifications/i }).first();
    await expect(bell).toHaveAttribute("aria-expanded", "false");
    await expect(bell).toHaveAttribute("aria-haspopup", "dialog");
    await bell.focus();
    await page.keyboard.press("Enter");
    const panel = page.getByRole("dialog", { name: /notifications panel/i });
    await expect(panel).toBeVisible();
    await expect(panel.getByRole("button", { name: /mark as read.*E2E keyboard notification/i })).toBeVisible();
    await panel.getByRole("button", { name: /mark as read.*E2E keyboard notification/i }).click();
    await expect(bell).toHaveAccessibleName("Notifications");
    await expect(panel.getByRole("button", { name: /mark as read/i })).toHaveCount(0);
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
    await expect(bell).toBeFocused();
  });

  test("keeps the header badge on the live unread count after the popover closes", async ({ authenticatedPage: page }) => {
    const notification = {
      id: "00000000-0000-0000-0000-00000000000b",
      workspaceId: "00000000-0000-0000-0000-00000000000c",
      projectId: null,
      generationJobId: null,
      assetId: null,
      type: "generation.completed",
      resourceTitle: "E2E badge lifecycle notification",
      createdAt: new Date().toISOString(),
      destination: "/activity",
    };
    let unread = true;
    const listBody = () => JSON.stringify({ items: [{ ...notification, isRead: !unread, readAt: unread ? null : new Date().toISOString() }], page: 1, pageSize: 20, totalCount: 1, totalPages: 1, unreadCount: unread ? 1 : 0 });
    // Register the broadest pattern first so the more specific unread-count
    // and read-all routes win for their URLs.
    await page.route("**/api/notifications?*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: listBody() });
    });
    await page.route("**/api/notifications/unread-count*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ unreadCount: unread ? 1 : 0 }) });
    });
    await page.route("**/api/notifications/read-all", async (route) => {
      unread = false;
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ read: true }) });
    });
    await page.goto("/notifications");
    const badge = page.locator(".notification-badge");
    await expect(badge.first()).toHaveText("1");
    const bell = page.getByRole("button", { name: /notifications/i }).first();
    // A count event scoped to another workspace must never move this user's badge.
    await page.evaluate(() => window.dispatchEvent(new CustomEvent("taslim:notification-unread-count", { detail: { workspaceId: "ffffffff-ffff-ffff-ffff-ffffffffffff", unreadCount: 0 } })));
    await expect(badge.first()).toHaveText("1");
    // Open the popover so the header badge adopts the panel snapshot, then close it.
    await bell.click();
    await expect(page.getByRole("dialog", { name: /notifications panel/i })).toBeVisible();
    await bell.click();
    await expect(page.getByRole("dialog", { name: /notifications panel/i })).toHaveCount(0);
    // Marking everything read from the full center publishes the live count;
    // after the popover closed, the panel snapshot may not freeze the badge.
    await page.getByRole("button", { name: /mark all as read|mark all read/i }).click();
    await expect(badge).toHaveCount(0);
    await expect(bell).toHaveAccessibleName("Notifications");
  });

  test("keeps the notification popover open when an unread notification cannot be marked read", async ({ authenticatedPage: page }) => {
    const notification = {
      id: "00000000-0000-0000-0000-000000000005",
      workspaceId: "00000000-0000-0000-0000-000000000006",
      projectId: null,
      generationJobId: "00000000-0000-0000-0000-000000000007",
      assetId: null,
      type: "generation.failed",
      resourceTitle: "E2E read failure notification",
      createdAt: new Date().toISOString(),
      readAt: null,
      isRead: false,
      destination: "/activity?jobId=00000000-0000-0000-0000-000000000007",
    };
    await page.route("**/api/notifications/unread-count*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ unreadCount: 1 }) });
    });
    await page.route("**/api/notifications?*", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ items: [notification], page: 1, pageSize: 6, totalCount: 1, totalPages: 1, unreadCount: 1 }),
      });
    });
    await page.route("**/api/notifications/*/read", async (route) => {
      await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ message: "temporarily unavailable" }) });
    });
    await page.goto("/projects");

    const bell = page.getByRole("button", { name: /notifications/i }).first();
    await bell.click();
    const panel = page.getByRole("dialog", { name: /notifications panel/i });
    await expect(panel).toBeVisible();
    await panel.getByRole("link", { name: /E2E read failure notification/i }).click();
    await expect(panel).toBeVisible();
    await expect(panel.getByRole("alert")).toContainText(/notification could not be updated/i);
    await expect(page).toHaveURL(/\/projects/);
  });
});
