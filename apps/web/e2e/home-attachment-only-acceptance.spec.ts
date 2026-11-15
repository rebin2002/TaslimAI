import { randomUUID } from "node:crypto";
import { E2E_PASSWORD, logoutInUi, registerInUi, test, expect } from "./fixtures";
import type { Route } from "@playwright/test";

/**
 * Authenticated Home -> Chat attachment-only acceptance. All write boundaries
 * after registration are intercepted; no message reaches a generation provider,
 * ledger, file store or customer charging service.
 */
const attachmentOnlyLocales = [
  { locale: "en", htmlLang: "en", prompt: "Please review the attached files." },
  { locale: "ar", htmlLang: "ar", prompt: "يرجى مراجعة الملفات المرفقة." },
  { locale: "ku", htmlLang: "ckb", prompt: "تکایە پێداچوونەوە بە فایلە هاوپێچکراوەکاندا بکە." },
] as const;

for (const { locale, htmlLang, prompt: expectedPrompt } of attachmentOnlyLocales) {
test(`Home attachment-only creates one provider-free Chat submission in ${locale}`, async ({ authenticatedPage: page }) => {
  const conversationId = "00000000-0000-4000-8000-000000000821";
  const fileId = "00000000-0000-4000-8000-000000000527";
  const seen: { conversation: number; upload: number; messages: { content: string; attachmentIds: string[] }[] } = {
    conversation: 0, upload: 0, messages: [],
  };

  await page.route("**/api/workspaces/*/conversations", async (route: Route) => {
    if (route.request().method() !== "POST") return route.continue();
    seen.conversation += 1;
    await route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({
        id: conversationId, workspaceId: "test-workspace", projectId: null,
        title: "Attachment review", status: "Active",
        createdAt: new Date(0).toISOString(), updatedAt: new Date(0).toISOString(), lastMessageAt: null,
      }),
    });
  });
  await page.route("**/api/workspaces/*/files", async (route: Route) => {
    if (route.request().method() !== "POST") return route.continue();
    seen.upload += 1;
    await route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ id: fileId, status: "Ready", originalFileName: "report.txt" }),
    });
  });
  await page.route("**/api/conversations/*/messages", async (route: Route) => {
    if (route.request().method() === "GET") {
      return route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    }
    if (route.request().method() !== "POST") return route.continue();
    const body = route.request().postDataJSON() as { content: string; attachmentIds: string[] };
    seen.messages.push({ content: body.content, attachmentIds: body.attachmentIds });
    await route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });

  // The destination is an entirely synthetic conversation; no database write
  // or generation is needed to verify the Home-to-Chat navigation.
  await page.route("**/api/conversations/" + conversationId, async (route: Route) => {
    if (route.request().method() !== "GET") return route.continue();
    await route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({
        id: conversationId, workspaceId: "test-workspace", projectId: null,
        title: "Attachment review", status: "Active",
        createdAt: new Date(0).toISOString(), updatedAt: new Date(0).toISOString(), lastMessageAt: null,
      }),
    });
  });
  await page.goto("/");
  await page.locator(".language-select select").selectOption(locale);
  await expect(page.locator("html")).toHaveAttribute("lang", htmlLang);
  const composer = page.locator(".home-composer");
  await expect(composer).toBeVisible();
  const submit = composer.locator(".home-composer-submit");
  await expect(submit).toBeDisabled();
  await composer.locator('input[type="file"]').setInputFiles({
    name: "report.txt", mimeType: "text/plain", buffer: Buffer.from("A local, non-sensitive test report."),
  });
  await expect(submit).toBeEnabled();
  // Chinese/Japanese/Korean IME Enter confirms a character candidate; it must
  // not create the conversation or dispatch a paid generation request.
  await composer.locator(".home-composer-input").evaluate((input) => {
    input.dispatchEvent(new KeyboardEvent("keydown", {
      key: "Enter", isComposing: true, bubbles: true, cancelable: true,
    }));
  });
  expect(seen.conversation).toBe(0);
  expect(seen.upload).toBe(0);
  expect(seen.messages).toEqual([]);
  await submit.click();
  await expect(page).toHaveURL(new RegExp("/chat/" + conversationId));
  expect(seen).toEqual({
    conversation: 1,
    upload: 1,
    messages: [{ content: expectedPrompt, attachmentIds: [fileId] }],
  });
});
}

/**
 * The browser can keep a private unsent File handle and text in React state.
 * Those belong to the authenticated principal only; neither should be
 * exposed or submitted after a same-tab account transition.
 */
test("Home does not carry local files or draft text into a different account", async ({ authenticatedPage: page }) => {
  await page.goto("/");
  const composer = page.locator(".home-composer");
  await expect(composer).toBeVisible();
  await composer.locator(".home-composer-input").fill("Private Account A instructions");
  await composer.locator('input[type="file"]').setInputFiles({
    name: "account-A-private-file.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("Private account A attachment (synthetic)."),
  });
  await expect(composer).toContainText("account-A-private-file.txt");
  await expect(composer.locator(".home-composer-submit")).toBeEnabled();

  await logoutInUi(page);
  await registerInUi(page, {
    displayName: "E2E Alternate Home Account",
    email: `e2e+home-switch-${randomUUID()}@example.test`,
    password: E2E_PASSWORD,
  });
  await page.goto("/");
  await expect(composer).toBeVisible();
  await expect(composer.locator(".home-composer-input")).toHaveValue("");
  await expect(composer).not.toContainText("account-A-private-file.txt");
  await expect(composer).not.toContainText("Private Account A instructions");
  await expect(composer.locator(".home-composer-submit")).toBeDisabled();
  expect(await composer.locator('input[type="file"]').evaluate((el) => (el as HTMLInputElement).files?.length ?? 0)).toBe(0);
});

/**
 * Once an authenticated Home has initiated a create, an account transition
 * must not continue its old asynchronous workflow into private file upload or
 * message submission. The initial create may already have reached the server;
 * it cannot be retroactively undone by the browser.
 */
test("Home does not upload or send an old account's files after pending create and logout", async ({ authenticatedPage: page }) => {
  const conversationId = "00000000-0000-4000-8000-000000000822";
  let notifyStarted: () => void = () => {};
  let releaseCreate: () => void = () => {};
  const started = new Promise<void>((resolve) => { notifyStarted = resolve; });
  const blockedCreate = new Promise<void>((resolve) => { releaseCreate = resolve; });
  let creations = 0;
  let uploads = 0;
  let messages = 0;

  await page.route("**/api/workspaces/*/conversations", async (route: Route) => {
    if (route.request().method() !== "POST") return route.continue();
    creations += 1;
    notifyStarted();
    await blockedCreate;
    // Navigation may cancel the old request before its delayed response can be
    // delivered. A failed fulfill in that case is expected, not a test failure.
    try {
      await route.fulfill({
        status: 200, contentType: "application/json",
        body: JSON.stringify({
          id: conversationId, workspaceId: "test-workspace", projectId: null,
          title: "Old account draft", status: "Active",
          createdAt: new Date(0).toISOString(), updatedAt: new Date(0).toISOString(), lastMessageAt: null,
        }),
      });
    } catch { /* browser abandoned the pre-logout request */ }
  });
  await page.route("**/api/workspaces/*/files", async (route: Route) => {
    if (route.request().method() !== "POST") return route.continue();
    uploads += 1;
    await route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });
  await page.route("**/api/conversations/*/messages", async (route: Route) => {
    if (route.request().method() !== "POST") return route.continue();
    messages += 1;
    await route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });

  await page.goto("/");
  const composer = page.locator(".home-composer");
  await expect(composer).toBeVisible();
  await composer.locator("input[type='file']").setInputFiles({
    name: "account-A-sensitive.txt", mimeType: "text/plain",
    buffer: Buffer.from("Synthetic private content; should not upload after logout."),
  });
  await composer.locator(".home-composer-submit").click();
  await started;
  expect(creations).toBe(1);
  expect(uploads).toBe(0);
  expect(messages).toBe(0);

  try {
    await logoutInUi(page);
    await registerInUi(page, {
      displayName: "E2E Post-create Account",
      email: `e2e+home-async-switch-${randomUUID()}@example.test`,
      password: E2E_PASSWORD,
    });
  } finally {
    releaseCreate();
  }
  await page.goto("/");
  await expect(composer).toBeVisible();
  await expect(composer.locator(".home-composer-submit")).toBeDisabled();
  await expect(composer).not.toContainText("account-A-sensitive.txt");
  // Allow any stale promise microtasks to settle before checking that there was
  // no old-account file upload, message send or navigation into its chat.
  await page.waitForTimeout(300);
  expect(uploads).toBe(0);
  expect(messages).toBe(0);
  await expect(page).toHaveURL(/\/$/);
});
