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
});
