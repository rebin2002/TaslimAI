import { randomUUID } from "node:crypto";
import { test as base, expect, type APIRequestContext, type Page } from "@playwright/test";

export const E2E_API_URL = (process.env.E2E_API_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
export const E2E_PASSWORD = process.env.E2E_TEST_PASSWORD ?? "E2eStrongPassword!123";

export type TestUser = {
  displayName: string;
  email: string;
  password: string;
};

export type AuthenticatedPage = Page & { testUser: TestUser };

type TaslimFixtures = {
  testUser: TestUser;
  authenticatedPage: AuthenticatedPage;
};

function makeTestUser(testId: string): TestUser {
  const testSlug = testId.replace(/[^a-z0-9]+/gi, "-").toLowerCase().slice(0, 24);
  const suffix = `${testSlug}-${randomUUID().slice(0, 12)}`;
  return {
    displayName: "E2E Test User",
    email: `e2e+${suffix}@example.test`,
    password: E2E_PASSWORD,
  };
}

async function waitForApp(page: Page, path: string) {
  await page.goto(path);
  await expect(page.locator("body")).toBeVisible();
}

export async function completeOnboarding(page: Page, intent: "project" | "chat" = "project") {
  const onboarding = page.getByRole("dialog");
  await expect(onboarding).toBeVisible();
  await expect(onboarding.locator("#onboarding-title")).toBeVisible();
  await onboarding.getByRole("button", { name: /continue/i }).click();
  await onboarding.getByRole("button", { name: /choose .*action/i }).click();
  const workflowButtons = onboarding.getByRole("button");
  if (intent === "chat") {
    await workflowButtons.filter({ hasText: /chat/i }).click();
  } else {
    await workflowButtons.filter({ hasText: /project/i }).click();
  }
  await expect(onboarding).toBeHidden();
}

export async function registerInUi(page: Page, user: TestUser, complete = true) {
  await waitForApp(page, "/register");
  await page.getByLabel(/display name/i).fill(user.displayName);
  await page.getByLabel(/email/i).fill(user.email);
  await page.getByRole("textbox", { name: /^password/i }).fill(user.password);
  await page.getByLabel(/confirm password/i).fill(user.password);
  await page.getByRole("button", { name: /create account/i }).click();
  await expect(page).toHaveURL(/\/projects/);
  if (complete) await completeOnboarding(page);
}

export async function loginInUi(page: Page, user: TestUser) {
  await waitForApp(page, "/login");
  await page.getByLabel(/email/i).fill(user.email);
  await page.getByRole("textbox", { name: /^password/i }).fill(user.password);
  await page.getByRole("button", { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/projects/);
}

export async function logoutInUi(page: Page) {
  await page.goto("/account");
  await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();
  await page.getByRole("button", { name: /log out/i }).click();
  await expect(page).toHaveURL(/(?:\/|\/login\?next=%2Faccount)$/);
}

async function getCsrf(request: APIRequestContext) {
  const response = await request.get(`${E2E_API_URL}/api/auth/csrf`);
  if (!response.ok()) throw new Error(`CSRF request failed: ${response.status()} ${await response.text()}`);
  return (await response.json() as { token: string }).token;
}

export async function apiJson<T = unknown>(
  request: APIRequestContext,
  method: string,
  path: string,
  data?: unknown,
  headers: Record<string, string> = {},
): Promise<T> {
  const csrfRequired = !["GET", "HEAD", "OPTIONS"].includes(method.toUpperCase());
  const csrf = csrfRequired ? await getCsrf(request) : undefined;
  const response = await request.fetch(`${E2E_API_URL}${path}`, {
    method,
    data,
    headers: {
      ...(data === undefined ? {} : { "Content-Type": "application/json" }),
      ...(csrf ? { "X-CSRF-TOKEN": csrf } : {}),
      ...headers,
    },
  });
  if (!response.ok()) throw new Error(`${method} ${path} failed: ${response.status()} ${await response.text()}`);
  if (response.status() === 204) return undefined as T;
  return await response.json() as T;
}

export async function createProject(page: Page, name = `E2E Project ${Date.now()}`) {
  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  return apiJson<{ id: string; name: string; workspaceId: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/projects`, {
    name,
    type: "Business",
    description: "Deterministic browser test project",
    instructions: "Use mock-safe test data only.",
    contextNotes: "Created by the Playwright E2E suite.",
  });
}

export const test = base.extend<TaslimFixtures>({
  testUser: async ({}, useFixture, testInfo) => {
    // Playwright names this lifecycle callback `use`; it is not a React hook.
    // eslint-disable-next-line react-hooks/rules-of-hooks
    await useFixture(makeTestUser(testInfo.testId));
  },
  authenticatedPage: async ({ page, testUser }, useFixture) => {
    (page as AuthenticatedPage).testUser = testUser;
    await registerInUi(page, testUser);
    // Playwright names this lifecycle callback `use`; it is not a React hook.
    // eslint-disable-next-line react-hooks/rules-of-hooks
    await useFixture(page as AuthenticatedPage);
    try {
      const session = await page.request.get(`${E2E_API_URL}/api/auth/me`);
      if (session.ok()) await apiJson(page.request, "POST", "/api/auth/logout");
    } catch {
      // The browser context may already be closed or intentionally logged out.
    }
  },
});

export { expect };
