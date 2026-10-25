import { test, expect } from "./fixtures";

const protectedEntryPoints = [
  { path: "/projects", label: "Projects" },
  { path: "/account", label: "Account" },
  { path: "/create/document", label: "Document Studio" },
  { path: "/search", label: "Global Search" },
  { path: "/account/admin/operations", label: "Admin Operations" },
] as const;

test.describe("anonymous protected-route boundary", () => {
  for (const entryPoint of protectedEntryPoints) {
    test(`${entryPoint.label} redirects before private content is exposed`, async ({ page }) => {
      await page.goto(entryPoint.path);

      await expect(page).toHaveURL(new RegExp(`/login\\?next=%2F${entryPoint.path.slice(1).replaceAll("/", "%2F")}$`));
      await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
      await expect(page.getByRole("heading", { name: new RegExp(entryPoint.label, "i") })).toHaveCount(0);
      await expect(page.locator("body")).not.toContainText(/provider details|workspace projects|administrator access required/i);
    });
  }
});
