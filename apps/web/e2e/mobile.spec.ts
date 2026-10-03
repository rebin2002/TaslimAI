import { test, expect } from "./fixtures";

test.describe("mobile navigation", () => {
  test("keeps the five-item bottom navigation usable at 390×844", async ({ authenticatedPage: page }) => {
    const viewport = page.viewportSize();
    expect(viewport).toEqual({ width: 390, height: 844 });

    const bottomNav = page.locator("nav.mobile-bottom-nav");
    const links = bottomNav.getByRole("link");
    await expect(links).toHaveCount(5);
    await expect(links.nth(0)).toHaveAttribute("href", "/");
    await expect(links.nth(1)).toHaveAttribute("href", "/projects");
    await expect(links.nth(2)).toHaveAttribute("href", "/create");
    await expect(links.nth(3)).toHaveAttribute("href", "/notifications");
    await expect(links.nth(4)).toHaveAttribute("href", "/account");
    await expect(bottomNav).toContainText(/home/i);
    await expect(bottomNav).toContainText(/projects/i);
    await expect(bottomNav).toContainText(/create/i);
    await expect(bottomNav).toContainText(/activity/i);
    await expect(bottomNav).toContainText(/account/i);

    for (const href of ["/", "/projects", "/create", "/notifications", "/account"]) {
      await page.goto(href);
      await expect(bottomNav).toBeVisible();
      await expect(page.locator("main")).toBeVisible();
    }
  });

  test("keeps the notification center readable and actionable on a narrow screen", async ({ authenticatedPage: page }) => {
    await page.route("**/api/notifications/unread-count*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ unreadCount: 0 }) });
    });
    await page.route("**/api/notifications?*", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items: [], page: 1, pageSize: 50, totalCount: 0, totalPages: 0, unreadCount: 0 }) });
    });
    await page.goto("/notifications");
    await expect(page.getByRole("heading", { name: "Notifications", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: /mark all read/i })).toBeDisabled();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 2);
    expect(overflow).toBe(true);
    await expect(page.locator(".notification-center-links > *")).toHaveCount(2);
  });

  test("keeps the Full Movie module bodies usable at 390×844", async ({ authenticatedPage: page }) => {
    test.setTimeout(180_000);
    await page.goto("/create/movie");
    await page.getByRole("button", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill("Mobile Movie Workspace");
    await page.getByLabel("Describe your movie").fill("A responsive module coverage project.");
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
    const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();
    for (const roomSlug of ["overview", "production-kit", "story", "cast", "world", "scenes", "storyboard", "production", "selects", "edit", "audio", "qc", "exports", "team"]) {
      await page.goto(`/create/movie/${projectId}/${roomSlug}`);
      await expect(page.locator("main")).toBeVisible({ timeout: 30_000 });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 2);
      expect(overflow, `${roomSlug} must not overflow horizontally on mobile`).toBe(true);
    }
  });
});
