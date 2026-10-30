import { test, expect } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";

test.describe("Image Studio server authentication", () => {
  test("redirects an anonymous HTTP request before Image Studio content is rendered", async ({ page }) => {
    const response = await page.request.get(new URL("/create/image", webUrl).toString(), { maxRedirects: 0 });

    expect(response.status()).toBe(307);
    expect(response.headers().location).toBe("/login?next=%2Fcreate%2Fimage");
    expect(await response.text()).not.toContain("image-studio-mark");

    await page.goto("/create/image");
    await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fimage$/);
    await expect(page.getByRole("heading", { name: "Image Studio" })).toHaveCount(0);
    await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
  });

  test("renders Image Studio for an authenticated session without starting generation", async ({ authenticatedPage: page }) => {
    const generationPosts: string[] = [];
    page.on("request", (request) => {
      const requestUrl = new URL(request.url());
      if (request.method() === "POST" && requestUrl.pathname.endsWith("/api/image-generation/jobs")) {
        generationPosts.push(request.url());
      }
    });

    const response = await page.goto("/create/image");

    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL(/\/create\/image$/);
    await expect(page.getByRole("heading", { name: "Image Studio" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: /describe your image/i })).toBeVisible();
    expect(generationPosts).toEqual([]);
  });
});
