import { test, expect, apiJson, E2E_API_URL } from "./fixtures";

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

  test("rejects a previously valid session after revocation before rendering Image Studio", async ({ authenticatedPage: page }) => {
    const priorAuthCookie = (await page.context().cookies(webUrl)).find((cookie) => cookie.name === "taslim.auth");
    expect(priorAuthCookie).toBeDefined();
    if (!priorAuthCookie) throw new Error("The authenticated fixture did not issue a session cookie.");

    await apiJson(page.request, "POST", "/api/auth/sessions/revoke");
    const refreshedAuthCookie = (await page.context().cookies(webUrl)).find((cookie) => cookie.name === "taslim.auth");
    expect(refreshedAuthCookie).toBeDefined();
    if (!refreshedAuthCookie) throw new Error("Session revocation did not refresh the current session cookie.");
    expect((await page.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(200);

    // Replay the pre-revocation cookie: the API must reject it, and the
    // server-rendered Image route must redirect before emitting Studio markup.
    await page.context().addCookies([priorAuthCookie]);
    expect((await page.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(401);
    const response = await page.request.get(new URL("/create/image", webUrl).toString(), { maxRedirects: 0 });
    expect(response.status()).toBe(307);
    expect(response.headers().location).toBe("/login?next=%2Fcreate%2Fimage");
    expect(await response.text()).not.toContain("image-studio-mark");
    await page.goto("/create/image");
    await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fimage$/);
    await expect(page.getByRole("heading", { name: "Image Studio" })).toHaveCount(0);
    await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();

    // Restore the fresh test-only session so the fixture's normal teardown can log it out.
    await page.context().addCookies([refreshedAuthCookie]);
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
    const prompt = page.getByRole("textbox", { name: "What would you like to create?" });
    await expect(prompt).toBeVisible();
    await expect(prompt).toHaveAttribute("placeholder", /Describe the image you have in mind/i);
    expect(generationPosts).toEqual([]);
  });
});
