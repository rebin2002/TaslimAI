import { test, expect } from "./fixtures";
import type { Route } from "@playwright/test";

/**
 * Authenticated Home -> Chat attachment-only acceptance. All write boundaries
 * after registration are intercepted; no message reaches a generation provider,
 * ledger, file store or customer charging service.
 */
test("Home attachment-only creates exactly one scoped Chat submission without a provider call", async ({ authenticatedPage: page }) => {
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
  const composer = page.locator(".home-composer");
  await expect(composer).toBeVisible();
  const submit = composer.locator(".home-composer-submit");
  await expect(submit).toBeDisabled();
  await composer.locator('input[type="file"]').setInputFiles({
    name: "report.txt", mimeType: "text/plain", buffer: Buffer.from("A local, non-sensitive test report."),
  });
  await expect(submit).toBeEnabled();
  await submit.click();
  await expect(page).toHaveURL(new RegExp("/chat/" + conversationId));
  expect(seen).toEqual({
    conversation: 1,
    upload: 1,
    messages: [{ content: "Please review the attached files.", attachmentIds: [fileId] }],
  });
});
