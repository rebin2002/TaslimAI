import { randomUUID } from "node:crypto";
import type { Route } from "@playwright/test";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";
import type { ChatMessage, Conversation } from "../src/lib/api";

test("Chat normal-send retry recovers a failed stream without duplicating the user message", async ({ page, testUser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires the disposable Taslim CI browser harness.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");
  test.setTimeout(45_000);

  const loopbackHosts = new Set(["localhost", "127.0.0.1", "::1"]);
  const apiUrl = new URL(E2E_API_URL);
  const webUrl = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000");
  for (const url of [apiUrl, webUrl]) {
    expect(url.protocol).toBe("http:");
    expect(loopbackHosts.has(url.hostname)).toBe(true);
  }

  const blockedExternalRequests: string[] = [];
  const guardLoopbackTraffic = async (route: Route) => {
    const url = new URL(route.request().url());
    if (url.protocol === "http:" && loopbackHosts.has(url.hostname)) {
      await route.fallback();
      return;
    }
    blockedExternalRequests.push(`${route.request().method()} ${url.origin}`);
    await route.abort("blockedbyclient");
  };
  await page.route("**/*", guardLoopbackTraffic);

  await registerInUi(page, testUser);
  expect(testUser.email).toMatch(/@example\.test$/);

  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const conversation = await apiJson<Conversation>(
    page.request,
    "POST",
    `/api/workspaces/${auth.personalWorkspace.id}/conversations`,
    { title: "E2E Normal Send Retry" },
  );

  const messageText = `Normal send retry ${randomUUID().slice(0, 8)}`;
  const assistantText = "Recovered normal-send response.";
  const internalFailureDetails = "MOCK_INTERNAL_FAILURE_DETAIL provider=not-real trace=TEST_ONLY";
  const createdAt = new Date().toISOString();
  const userMessageId = randomUUID();
  const assistantMessageId = randomUUID();
  const submissions: Array<Record<string, unknown>> = [];

  await page.route(`**/api/conversations/${conversation.id}/messages/stream`, async (route) => {
    const request = route.request();
    if (request.method() !== "POST") {
      await route.abort();
      return;
    }

    const payload = request.postDataJSON() as Record<string, unknown>;
    submissions.push(payload);
    expect(payload.content).toBe(messageText);
    expect(typeof payload.requestId).toBe("string");
    expect(payload.requestId).toBeTruthy();
    expect(request.headers()["x-csrf-token"]).toBeTruthy();

    const userMessage: ChatMessage = {
      id: userMessageId,
      conversationId: conversation.id,
      role: "User",
      content: messageText,
      status: "Completed",
      createdAt,
      sequence: 1,
    };
    const pendingAssistant: ChatMessage = {
      id: assistantMessageId,
      conversationId: conversation.id,
      role: "Assistant",
      content: "",
      status: "Pending",
      createdAt,
      sequence: 2,
    };

    const startedEvent = `event: message.started\ndata: ${JSON.stringify({ conversation, userMessage, assistantMessage: pendingAssistant })}\n\n`;
    const terminalEvent = submissions.length === 1
      ? `event: message.failed\ndata: ${JSON.stringify({ code: "AI_GENERATION_FAILED", message: internalFailureDetails })}\n\n`
      : `event: message.completed\ndata: ${JSON.stringify({
        conversation,
        userMessage,
        assistantMessage: { ...pendingAssistant, status: "Completed", content: assistantText },
      })}\n\n`;

    await route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: startedEvent + terminalEvent,
    });
  });

  await page.goto(`/chat/${conversation.id}`);
  await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();
  await page.getByLabel("Message Taslim...").fill(messageText);

  const streamResponse = () => page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "POST"
      && url.origin === apiUrl.origin
      && url.pathname === `/api/conversations/${conversation.id}/messages/stream`;
  });

  const firstResponse = streamResponse();
  await page.getByRole("button", { name: /send message/i }).click();
  expect((await firstResponse).status()).toBe(200);

  const errorAlert = page.locator(".chat-inline-error");
  await expect(errorAlert).toBeVisible();
  await expect(errorAlert).not.toContainText(/MOCK_INTERNAL_FAILURE_DETAIL|trace=TEST_ONLY|api.?key|secret|stack trace|exception/i);
  await expect(page.getByText(messageText, { exact: true })).toHaveCount(1);
  const retryButton = errorAlert.getByRole("button", { name: /^retry$/i });
  await expect(retryButton).toBeVisible();

  const retryResponse = streamResponse();
  await retryButton.click();
  expect((await retryResponse).status()).toBe(200);

  await expect(page.getByText(assistantText, { exact: true })).toBeVisible();
  await expect(page.getByText(messageText, { exact: true })).toHaveCount(1);
  await expect(errorAlert).toHaveCount(0);
  expect(submissions).toHaveLength(2);
  expect(submissions[1].content).toBe(messageText);
  expect(submissions[1].requestId).toBe(submissions[0].requestId);
  expect(submissions[1].attachmentIds).toEqual(submissions[0].attachmentIds);
  expect(blockedExternalRequests).toEqual([]);
});
