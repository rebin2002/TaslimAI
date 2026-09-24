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
});
