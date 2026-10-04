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

  test("cancels in-flight conversation reads when Chat unmounts during navigation", async ({ authenticatedPage: page }) => {
    test.setTimeout(30_000);
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const conversation = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E Cancelled Chat Read" });
    let delayedRequestSeen = false;
    let delayedRequestFailed = false;
    const conversationUrl = `/api/conversations/${conversation.id}`;
    page.on("requestfailed", (request) => {
      if (request.url().includes(conversationUrl)) delayedRequestFailed = true;
    });
    await page.route(`**/api/conversations/${conversation.id}`, async (route) => {
      delayedRequestSeen = true;
      await new Promise((resolve) => setTimeout(resolve, 5_000));
      try {
        await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(conversation) });
      } catch {
        // The expected route-transition abort closes this intercepted request.
      }
    });
    await page.goto(`/chat/${conversation.id}`);
    await expect.poll(() => delayedRequestSeen, { timeout: 10_000 }).toBe(true);
    await page.getByRole("link", { name: /^projects$/i }).first().click();
    await expect(page).toHaveURL(/\/projects$/);
    await expect.poll(() => delayedRequestFailed, { timeout: 5_000 }).toBe(true);
  });

  test("does not apply late stream events after switching conversations", async ({ authenticatedPage: page }) => {
    test.setTimeout(30_000);
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const oldConversation = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E Old Stream" });
    const newConversation = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E New Conversation" });
    let streamStarted = false;
    const oldUserMessage = {
      id: "00000000-0000-0000-0000-000000000101",
      conversationId: oldConversation.id,
      role: "User",
      content: "old request",
      status: "Completed",
      createdAt: new Date().toISOString(),
      sequence: 1,
    };
    const oldAssistantMessage = {
      id: "00000000-0000-0000-0000-000000000102",
      conversationId: oldConversation.id,
      role: "Assistant",
      content: "late old SSE response",
      status: "Completed",
      createdAt: new Date().toISOString(),
      sequence: 2,
    };
    await page.route(`**/api/conversations/${oldConversation.id}/messages/stream`, async (route) => {
      streamStarted = true;
      await new Promise((resolve) => setTimeout(resolve, 1_000));
      try {
        await route.fulfill({
          status: 200,
          headers: { "content-type": "text/event-stream" },
          body: [
            "event: message.started",
            `data: ${JSON.stringify({ userMessage: oldUserMessage, assistantMessage: { ...oldAssistantMessage, content: "" }, conversation: oldConversation })}`,
            "",
            "event: message.delta",
            `data: ${JSON.stringify({ messageId: oldAssistantMessage.id, delta: oldAssistantMessage.content })}`,
            "",
            "event: message.completed",
            `data: ${JSON.stringify({ userMessage: oldUserMessage, assistantMessage: oldAssistantMessage, conversation: oldConversation })}`,
            "",
          ].join("\n"),
        });
      } catch {
        // A route transition may abort the intercepted request before the late event is delivered.
      }
    });

    // Seed a real browser history entry. Back is handled by Next's router and
    // keeps the Chat component mounted while the conversation param changes.
    await page.goto(`/chat/${newConversation.id}`);
    await page.goto(`/chat/${oldConversation.id}`);
    await expect(page.getByRole("heading", { name: "E2E Old Stream" })).toBeVisible();
    await page.getByLabel("Message Taslim...").fill("start old stream");
    await page.getByRole("button", { name: /send message/i }).click();
    await expect.poll(() => streamStarted, { timeout: 10_000 }).toBe(true);

    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`/chat/${newConversation.id}$`));
    await expect(page.getByRole("heading", { name: "E2E New Conversation" })).toBeVisible({ timeout: 10_000 });
    await page.waitForTimeout(1_500);
    await expect(page.getByText("late old SSE response")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "E2E New Conversation" })).toBeVisible();
  });
});
