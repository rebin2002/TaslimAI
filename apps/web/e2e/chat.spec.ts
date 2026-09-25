import { test, expect, apiJson, createProject } from "./fixtures";

test.describe("chat journeys", () => {
  test("starts Chat, keeps conversation navigation persistent, and renames a conversation", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const conversation = await apiJson<{ id: string; title: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, {
      title: "E2E Persistent Conversation",
    });

    await page.goto("/chat");
    await expect(page.getByRole("heading", { name: /chat/i }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: /new chat/i }).first()).toBeVisible();
    await page.goto(`/chat/${conversation.id}`);
    await expect(page.getByRole("heading", { name: "E2E Persistent Conversation" })).toBeVisible();
    await page.reload();
    await expect(page.getByRole("heading", { name: "E2E Persistent Conversation" })).toBeVisible();

    await page.getByRole("button", { name: /^rename$/i }).click();
    const renameInput = page.locator(".chat-rename input");
    await expect(renameInput).toBeVisible();
    await renameInput.fill("E2E Renamed Conversation");
    await page.getByRole("button", { name: /save title/i }).click();
    await expect(page.getByRole("heading", { name: "E2E Renamed Conversation" })).toBeVisible();
  });

  test("archives and safely deletes a conversation", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const conversation = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E Archive Me" });
    await page.goto(`/chat/${conversation.id}`);
    await page.getByRole("button", { name: /^archive$/i }).click();
    await expect(page).toHaveURL(/\/chat$/);
    await expect(page.getByText("E2E Archive Me")).toHaveCount(0);

    const removable = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E Delete Me" });
    await page.goto(`/chat/${removable.id}`);
    await page.getByRole("button", { name: /^delete$/i }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("heading", { name: /delete this conversation/i })).toBeVisible();
    await dialog.getByRole("button", { name: /^delete$/i }).click();
    await expect(page).toHaveURL(/\/chat$/);
    await expect(page.getByText("E2E Delete Me")).toHaveCount(0);
  });

  test("preserves a project context and supports file attachment without provider calls", async ({ authenticatedPage: page }) => {
    const project = await createProject(page, "E2E Attachment Project");
    await page.goto(`/chat?projectId=${project.id}`);
    await expect(page.getByLabel("Project context")).toHaveValue(project.id);

    await page.locator('input[type="file"]').setInputFiles({
      name: "e2e-notes.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("Deterministic E2E attachment content."),
    });
    await expect(page.getByText("e2e-notes.txt")).toBeVisible();
    await page.getByRole("button", { name: /clear all/i }).click();
    await expect(page.getByText("e2e-notes.txt")).toHaveCount(0);
  });

  test("renders a controlled safe error when the provider is unavailable", async ({ authenticatedPage: page }) => {
    await page.route("**/api/conversations/*/messages/stream", async (route) => {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: { code: "PROVIDER_UNAVAILABLE", message: "The AI provider is not available in this test." } }),
      });
    });
    await page.goto("/chat");
    await page.getByLabel("Message Taslim...").fill("E2E provider safety check");
    await page.getByRole("button", { name: /send message/i }).click();
    const errorAlert = page.locator(".chat-inline-error");
    await expect(errorAlert).toBeVisible({ timeout: 15_000 });
    await expect(errorAlert).not.toContainText(/stack|exception|api key|secret/i);
  });
});
