import { randomUUID } from "node:crypto";
import { test, expect, apiJson, registerInUi, E2E_API_URL, E2E_PASSWORD, type TestUser } from "./fixtures";

type PersistedChatMessage = {
  id: string;
  role: string;
  content: string;
  status: string;
  isTestResponse: boolean;
};

test("authenticated Chat persists a mock response after reload and denies another user", async ({ authenticatedPage: page, browser }) => {
  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const conversation = await apiJson<{ id: string; title: string }>(
    page.request,
    "POST",
    `/api/workspaces/${auth.personalWorkspace.id}/conversations`,
    { title: "E2E Chat Persistence Smoke" },
  );
  const userText = `Hello Taslim, E2E persisted message ${randomUUID()}`;
  const mockReply = "Hello! Taslim Chat is connected and ready.";
  const messagesPath = `/api/conversations/${conversation.id}/messages`;

  await page.goto(`/chat/${conversation.id}`);
  await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();
  await page.getByLabel("Message Taslim...").fill(userText);
  await page.getByRole("button", { name: /send message/i }).click();
  await expect(page.getByText(userText, { exact: true })).toBeVisible();
  await expect(page.getByText(mockReply, { exact: true })).toBeVisible({ timeout: 15_000 });

  const beforeReload = await apiJson<PersistedChatMessage[]>(page.request, "GET", messagesPath);
  expect(beforeReload.map((message) => message.role)).toEqual(["User", "Assistant"]);
  expect(beforeReload[0]).toMatchObject({ content: userText, status: "Completed" });
  expect(beforeReload[1]).toMatchObject({ content: mockReply, status: "Completed", isTestResponse: true });

  await page.reload();
  await expect(page.getByText(userText, { exact: true })).toBeVisible();
  await expect(page.getByText(mockReply, { exact: true })).toBeVisible();
  const afterReload = await apiJson<PersistedChatMessage[]>(page.request, "GET", messagesPath);
  expect(afterReload.map((message) => message.id)).toEqual(beforeReload.map((message) => message.id));

  const otherUser: TestUser = {
    displayName: "E2E Chat Privacy User",
    email: `e2e+chat-privacy-${randomUUID().slice(0, 12)}@example.test`,
    password: E2E_PASSWORD,
  };
  const otherContext = await browser.newContext({ baseURL: new URL(page.url()).origin });
  try {
    const otherPage = await otherContext.newPage();
    await registerInUi(otherPage, otherUser);
    const otherSession = await otherPage.request.get(`${E2E_API_URL}/api/auth/me`);
    expect(otherSession.status()).toBe(200);

    await otherPage.goto(`/chat/${conversation.id}`);
    await expect(otherPage.getByRole("heading", { level: 3 })).toBeVisible();
    await expect(otherPage.getByText(userText, { exact: true })).toHaveCount(0);
    await expect(otherPage.getByText(mockReply, { exact: true })).toHaveCount(0);

    const [conversationResponse, messagesResponse] = await Promise.all([
      otherPage.request.get(`${E2E_API_URL}/api/conversations/${conversation.id}`),
      otherPage.request.get(`${E2E_API_URL}${messagesPath}`),
    ]);
    expect(conversationResponse.status()).toBe(404);
    expect(messagesResponse.status()).toBe(404);
    const [conversationBody, messagesBody] = await Promise.all([
      conversationResponse.text(),
      messagesResponse.text(),
    ]);
    expect(conversationBody).not.toContain(userText);
    expect(conversationBody).not.toContain(mockReply);
    expect(messagesBody).not.toContain(userText);
    expect(messagesBody).not.toContain(mockReply);
  } finally {
    await otherContext.close();
  }
});
