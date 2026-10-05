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

  test("keeps Chat context cards, studio handoff cards, and composer spaced at desktop width", async ({ authenticatedPage: page }) => {
    await page.setViewportSize({ width: 1848, height: 826 });
    await page.goto("/chat");
    await expect(page.getByRole("heading", { name: /what would you like to explore/i })).toBeVisible();

    const contextBar = page.locator(".chat-context-bar");
    await expect(contextBar).toBeVisible();
    await expect(contextBar.locator(":scope > .chat-context-item")).toHaveCount(3);
    await expect(page.locator(".chat-creator-handoff")).toBeVisible();
    await expect(page.locator(".chat-creator-handoff > div > button")).toHaveCount(5);

    const layout = await page.evaluate(() => {
      const context = document.querySelector<HTMLElement>(".chat-context-bar");
      const handoff = document.querySelector<HTMLElement>(".chat-creator-handoff");
      const composer = document.querySelector<HTMLElement>(".chat-composer");
      const empty = document.querySelector<HTMLElement>(".chat-empty");
      return {
        contextDisplay: context ? getComputedStyle(context).display : "",
        contextGap: context ? getComputedStyle(context).gap : "",
        contextWidths: context ? [...context.children].map((item) => Math.round(item.getBoundingClientRect().width)) : [],
        handoffDisplay: handoff ? getComputedStyle(handoff).display : "",
        handoffGap: handoff ? getComputedStyle(handoff).gap : "",
        handoffButtonWidths: handoff ? [...handoff.querySelectorAll("button")].map((item) => Math.round(item.getBoundingClientRect().width)) : [],
        composerHeight: composer ? Math.round(composer.getBoundingClientRect().height) : 0,
        emptyMinHeight: empty ? getComputedStyle(empty).minHeight : "",
      };
    });

    expect(layout.contextDisplay).toBe("grid");
    expect(layout.contextGap).toBe("9px");
    expect(layout.contextWidths.every((width) => width > 180)).toBe(true);
    expect(layout.handoffDisplay).toBe("flex");
    expect(layout.handoffGap).toBe("12px");
    expect(layout.handoffButtonWidths.every((width) => width > 55)).toBe(true);
    expect(layout.composerHeight).toBeLessThan(150);
    expect(layout.emptyMinHeight).toBe("250px");

    await page.screenshot({ path: "test-results/chat-empty-layout-desktop.png" });
  });

  test("keeps Chat context and handoff controls readable in mobile RTL", async ({ authenticatedPage: page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/projects");
    await page.locator(".language-select select").first().selectOption("ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await page.goto("/chat");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.locator(".chat-context-item")).toHaveCount(3);
    await expect(page.locator(".chat-creator-handoff > div > button")).toHaveCount(5);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 2)).toBe(true);
    await expect.poll(() => page.locator(".chat-context-bar").evaluate((element) => getComputedStyle(element).gridTemplateColumns.trim().split(/\s+/).length)).toBe(1);
    await page.screenshot({ path: "test-results/chat-empty-layout-mobile-rtl.png" });
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

  test("retries a failed regeneration without adding a duplicate user message", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const conversation = await apiJson<{ id: string }>(page.request, "POST", `/api/workspaces/${auth.personalWorkspace.id}/conversations`, { title: "E2E Regeneration Retry" });
    const initial = await apiJson<{ conversation: Record<string, unknown>; userMessage: Record<string, unknown>; assistantMessage: Record<string, unknown> }>(page.request, "POST", `/api/conversations/${conversation.id}/messages`, { content: "Give me a safe test response." });
    let attempts = 0;
    let firstRequestId: string | undefined;
    let secondRequestId: string | undefined;
    await page.route(`**/api/conversations/${conversation.id}/messages/*/regenerate`, async (route) => {
      attempts += 1;
      const payload = route.request().postDataJSON() as { requestId?: string };
      if (attempts === 1) firstRequestId = payload.requestId;
      else secondRequestId = payload.requestId;
      const assistantMessage = { ...initial.assistantMessage, id: "regenerated-assistant-1", status: "Pending", content: "" };
      const terminalAssistant = { ...assistantMessage, status: "Completed", content: "Recovered regeneration response." };
      const body = attempts === 1
        ? `event: message.started\ndata: ${JSON.stringify({ conversation: initial.conversation, userMessage: initial.userMessage, assistantMessage })}\n\nevent: message.failed\ndata: ${JSON.stringify({ code: "AI_GENERATION_FAILED", message: "safe failure" })}\n\n`
        : `event: message.started\ndata: ${JSON.stringify({ conversation: initial.conversation, userMessage: initial.userMessage, assistantMessage })}\n\nevent: message.completed\ndata: ${JSON.stringify({ conversation: initial.conversation, userMessage: initial.userMessage, assistantMessage: terminalAssistant })}\n\n`;
      await route.fulfill({ status: 200, contentType: "text/event-stream", body });
    });
    await page.goto(`/chat/${conversation.id}`);
    await expect(page.getByRole("heading", { name: "E2E Regeneration Retry" })).toBeVisible();
    await page.getByRole("button", { name: /regenerate/i }).click();
    await expect(page.locator(".chat-inline-error")).toBeVisible();
    await page.getByRole("button", { name: /^retry$/i }).first().click();
    await expect(page.getByText("Recovered regeneration response.")).toBeVisible();
    await expect(page.getByText("Give me a safe test response.")).toHaveCount(1);
    expect(firstRequestId).toBeTruthy();
    expect(secondRequestId).toBe(firstRequestId);
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
