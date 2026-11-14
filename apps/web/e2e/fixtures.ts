import { randomUUID } from "node:crypto";
import { test as base, expect, type APIRequestContext, type Page, type Request, type Response } from "@playwright/test";
import { translate, type Locale } from "../src/lib/i18n";

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

type OnboardingTraffic = {
  csrfRequests: Request[];
  csrfResponses: Response[];
  completionRequests: Request[];
  completionResponses: Response[];
  authMeResponses: Response[];
};

function captureOnboardingTraffic(page: Page) {
  const traffic: OnboardingTraffic = {
    csrfRequests: [],
    csrfResponses: [],
    completionRequests: [],
    completionResponses: [],
    authMeResponses: [],
  };
  const apiOrigin = new URL(E2E_API_URL).origin;
  const onRequest = (request: Request) => {
    const url = new URL(request.url());
    if (url.origin !== apiOrigin) return;
    if (url.pathname === "/api/auth/csrf" && request.method() === "GET") traffic.csrfRequests.push(request);
    if (url.pathname === "/api/auth/onboarding/complete" && request.method() === "POST") traffic.completionRequests.push(request);
  };
  const onResponse = (response: Response) => {
    const url = new URL(response.url());
    if (url.origin !== apiOrigin) return;
    const method = response.request().method();
    if (url.pathname === "/api/auth/csrf" && method === "GET") traffic.csrfResponses.push(response);
    if (url.pathname === "/api/auth/onboarding/complete" && method === "POST") traffic.completionResponses.push(response);
    if (url.pathname === "/api/auth/me" && method === "GET") traffic.authMeResponses.push(response);
  };
  page.on("request", onRequest);
  page.on("response", onResponse);
  return {
    traffic,
    stop: () => {
      page.off("request", onRequest);
      page.off("response", onResponse);
    },
  };
}

export async function completeOnboarding(page: Page, traffic: OnboardingTraffic) {
  const onboarding = page.getByRole("dialog");
  await expect(onboarding).toBeVisible();
  const heading = onboarding.locator("#onboarding-title");
  const progress = onboarding.getByRole("progressbar");
  await expect(heading).toBeVisible();
  await expect(onboarding.locator("[data-dialog-autofocus]")).toBeFocused();
  const assertStep = async (step: number) => {
    await expect(progress).toHaveAttribute("aria-valuemin", "1");
    await expect(progress).toHaveAttribute("aria-valuemax", "3");
    await expect(progress).toHaveAttribute("aria-valuenow", String(step));
    await expect(heading).toBeFocused();
  };
  await onboarding.getByRole("button", { name: /continue/i }).click();
  await assertStep(2);
  await onboarding.getByRole("button", { name: /choose .*action/i }).click();
  await assertStep(3);

  const lang = await page.locator("html").getAttribute("lang");
  const locale: Locale = lang === "ar" ? "ar" : lang === "ckb" || lang === "ku" ? "ku" : "en";
  const choices = onboarding.locator(".onboarding-workflow");
  await expect(choices).toHaveCount(5);
  const expectedChoiceNames = [
    translate(locale, "onboarding.workflow.chat.title"),
    translate(locale, "onboarding.workflow.image.title"),
    translate(locale, "onboarding.workflow.document.title"),
    translate(locale, "onboarding.workflow.presentation.title"),
    translate(locale, "navigation.movieStudio"),
  ].sort();
  const visibleChoiceNames = (await choices.locator("strong").allTextContents()).map((name) => name.trim()).sort();
  expect(visibleChoiceNames).toEqual(expectedChoiceNames);

  const chatChoice = choices.filter({ hasText: translate(locale, "onboarding.workflow.chat.title") });
  await expect(chatChoice).toHaveCount(1);
  await chatChoice.focus();
  await expect(chatChoice).toBeFocused();
  expect(await onboarding.evaluate((dialog) => dialog.contains(document.activeElement))).toBe(true);
  await page.keyboard.press("Enter");
  await expect(onboarding).toBeHidden();
  await expect(page).toHaveURL((url) => url.pathname === "/chat");

  expect(traffic.csrfRequests.length).toBeGreaterThan(0);
  const csrfResponse = traffic.csrfResponses.at(-1);
  expect(csrfResponse?.ok()).toBe(true);
  const csrfBody = await csrfResponse!.json() as { token?: unknown };
  expect(typeof csrfBody.token).toBe("string");
  expect(traffic.completionRequests).toHaveLength(1);
  const completionRequest = traffic.completionRequests[0];
  expect(completionRequest.method()).toBe("POST");
  expect(new URL(completionRequest.url()).pathname).toBe("/api/auth/onboarding/complete");
  expect(await completionRequest.headerValue("x-csrf-token")).toBe(csrfBody.token);
  expect(completionRequest.postDataJSON()).toMatchObject({ intent: "chat" });
  expect(traffic.completionResponses).toHaveLength(1);
  expect(traffic.completionResponses[0].ok()).toBe(true);

  const authMeResponsesBeforeReload = traffic.authMeResponses.length;
  await page.reload();
  await expect(page).toHaveURL((url) => url.pathname === "/chat");
  await expect.poll(() => traffic.authMeResponses.length).toBeGreaterThan(authMeResponsesBeforeReload);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const persistedAuth = await traffic.authMeResponses.at(-1)!.json() as {
    user?: { onboardingCompletedAt?: string | null; onboardingIntent?: string | null };
  };
  expect(persistedAuth.user?.onboardingCompletedAt).toBeTruthy();
  expect(persistedAuth.user?.onboardingIntent).toBe("chat");
}

export async function registerInUi(page: Page, user: TestUser, complete = true) {
  const observer = complete ? captureOnboardingTraffic(page) : null;
  try {
    await waitForApp(page, "/register");
    await page.getByLabel(/display name/i).fill(user.displayName);
    await page.getByLabel(/email/i).fill(user.email);
    await page.getByRole("textbox", { name: /^password/i }).fill(user.password);
    await page.getByLabel(/confirm password/i).fill(user.password);
    await page.getByRole("button", { name: /create account/i }).click();
    await expect(page).toHaveURL(/\/projects/);
    if (complete && observer) {
      await completeOnboarding(page, observer.traffic);
      await page.goto("/projects");
      await expect(page).toHaveURL(/\/projects/);
      await expect(page.getByRole("dialog")).toHaveCount(0);
    }
  } finally {
    observer?.stop();
  }
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
