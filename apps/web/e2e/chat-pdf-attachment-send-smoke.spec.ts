import { randomUUID } from "node:crypto";
import type { Route } from "@playwright/test";
import { apiJson, E2E_API_URL, expect, registerInUi, type TestUser, test } from "./fixtures";

type StoredFile = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  conversationId: string | null;
  originalFileName: string;
  contentType: string;
  storageProvider: string;
  status: string;
  textExtractionStatus: string;
  extractedTextLength: number | null;
};

type PersistedChatMessage = {
  id: string;
  role: string;
  content: string;
  status: string;
  isTestResponse: boolean;
};

function makePdf(text: string): Buffer {
  const escaped = text.replace(/\\/g, "\\\\").replace(/\(/g, "\\(").replace(/\)/g, "\\)");
  const streamText = `BT /F1 12 Tf 72 720 Td (${escaped}) Tj ET`;
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    `<< /Length ${Buffer.byteLength(streamText, "ascii")} >>\nstream\n${streamText}\nendstream`,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
  ];

  let content = "%PDF-1.4\n";
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(content, "ascii"));
    content += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });

  const xrefOffset = Buffer.byteLength(content, "ascii");
  content += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const offset of offsets.slice(1)) content += `${String(offset).padStart(10, "0")} 00000 n \n`;
  content += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`;
  return Buffer.from(content, "ascii");
}

test("authenticated Chat sends a locally stored PDF, persists the exchange, and keeps it owner-scoped", async ({ page, testUser, browser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires the disposable Taslim CI local-storage and mock-provider harness.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");

  const loopbackHosts = new Set(["localhost", "127.0.0.1", "::1"]);
  const apiUrl = new URL(E2E_API_URL);
  const webUrl = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000");
  for (const url of [apiUrl, webUrl]) {
    expect(url.protocol).toBe("http:");
    expect(loopbackHosts.has(url.hostname)).toBe(true);
  }

  const blockedExternalRequests: string[] = [];
  const directStoragePuts: string[] = [];
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
  page.on("request", request => {
    if (request.method() === "PUT") directStoragePuts.push(new URL(request.url()).origin);
  });

  let ownerRegistered = false;
  let otherContext: Awaited<ReturnType<typeof browser.newContext>> | undefined;
  try {
    await registerInUi(page, testUser);
    ownerRegistered = true;
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const conversation = await apiJson<{ id: string; title: string }>(
      page.request,
      "POST",
      `/api/workspaces/${auth.personalWorkspace.id}/conversations`,
      { title: "E2E PDF Chat Attachment" },
    );
    const marker = `PDF_CHAT_${randomUUID().replace(/-/g, "").slice(0, 16)}`;
    const fileName = `chat-attachment-${randomUUID().slice(0, 12)}.pdf`;
    const pdfBytes = makePdf(marker);

    await page.goto(`/chat/${conversation.id}`);
    await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();
    const uploadResponsePromise = page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === "POST"
        && url.origin === apiUrl.origin
        && url.pathname === `/api/workspaces/${auth.personalWorkspace.id}/files`;
    });
    await page.locator(".chat-composer input[type=file]").setInputFiles({
      name: fileName,
      mimeType: "application/pdf",
      buffer: pdfBytes,
    });
    const uploadResponse = await uploadResponsePromise;
    const uploadResponseBody = await uploadResponse.text();
    expect(uploadResponse.status(), uploadResponseBody).toBe(201);
    expect(uploadResponse.request().headers()["x-csrf-token"]).toBeTruthy();
    expect(uploadResponse.request().postData() ?? "").toContain(conversation.id);

    const uploaded = JSON.parse(uploadResponseBody) as StoredFile;
    expect(uploaded).toMatchObject({
      workspaceId: auth.personalWorkspace.id,
      projectId: null,
      conversationId: conversation.id,
      originalFileName: fileName,
      contentType: "application/pdf",
      storageProvider: "Local",
      status: "Ready",
      textExtractionStatus: "Ready",
    });
    expect(uploaded.extractedTextLength).toBeGreaterThan(0);
    await expect(page.locator(".chat-attachment-chip").getByText(fileName, { exact: true })).toBeVisible();

    const userText = `Hello Taslim, please review this local PDF ${marker}`;
    const streamPath = `/api/conversations/${conversation.id}/messages/stream`;
    const sendRequestPromise = page.waitForRequest(request => request.method() === "POST" && new URL(request.url()).pathname === streamPath);
    const sendResponsePromise = page.waitForResponse(response => response.request().method() === "POST" && new URL(response.url()).pathname === streamPath);
    await page.getByLabel("Message Taslim...").fill(userText);
    await page.getByRole("button", { name: /send message/i }).click();
    const sendRequest = await sendRequestPromise;
    const sendResponse = await sendResponsePromise;
    expect(sendResponse.status()).toBe(200);
    expect(sendRequest.headers()["x-csrf-token"]).toBeTruthy();
    expect(sendRequest.postDataJSON()).toMatchObject({ content: userText, attachmentIds: [uploaded.id] });
    await expect(page.locator(".chat-message-user").getByText(userText, { exact: true })).toBeVisible();
    await expect(page.getByText("Hello! Taslim Chat is connected and ready.", { exact: true })).toBeVisible({ timeout: 15_000 });

    const messagesPath = `/api/conversations/${conversation.id}/messages`;
    const beforeReload = await apiJson<PersistedChatMessage[]>(page.request, "GET", messagesPath);
    expect(beforeReload.map(message => message.role)).toEqual(["User", "Assistant"]);
    expect(beforeReload[0]).toMatchObject({ content: userText, status: "Completed" });
    expect(beforeReload[1]).toMatchObject({ status: "Completed", isTestResponse: true });

    await page.reload();
    await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();
    await expect(page.getByText(userText, { exact: true })).toBeVisible();
    await expect(page.getByText("Hello! Taslim Chat is connected and ready.", { exact: true })).toBeVisible();
    const afterReload = await apiJson<PersistedChatMessage[]>(page.request, "GET", messagesPath);
    expect(afterReload.map(message => message.id)).toEqual(beforeReload.map(message => message.id));

    const ownerReadback = await page.request.get(`${E2E_API_URL}/api/files/${uploaded.id}`);
    expect(ownerReadback.status()).toBe(200);
    const ownerReadbackBody = await ownerReadback.text();
    expect(JSON.parse(ownerReadbackBody) as StoredFile).toMatchObject({
      id: uploaded.id,
      conversationId: conversation.id,
      storageProvider: "Local",
      status: "Ready",
      textExtractionStatus: "Ready",
    });
    expect(ownerReadbackBody).not.toContain(marker);

    const otherUser: TestUser = {
      displayName: "E2E PDF Chat Privacy User",
      email: `e2e+pdf-chat-privacy-${randomUUID().slice(0, 12)}@example.test`,
      password: testUser.password,
    };
    otherContext = await browser.newContext({ baseURL: webUrl.origin });
    const otherPage = await otherContext.newPage();
    await otherPage.route("**/*", guardLoopbackTraffic);
    await registerInUi(otherPage, otherUser);
    const [deniedFile, deniedConversation, deniedMessages] = await Promise.all([
      otherPage.request.get(`${E2E_API_URL}/api/files/${uploaded.id}`),
      otherPage.request.get(`${E2E_API_URL}/api/conversations/${conversation.id}`),
      otherPage.request.get(`${E2E_API_URL}${messagesPath}`),
    ]);
    expect(deniedFile.status()).toBe(403);
    expect(deniedConversation.status()).toBe(404);
    expect(deniedMessages.status()).toBe(404);
    const deniedBodies = await Promise.all([deniedFile.text(), deniedConversation.text(), deniedMessages.text()]);
    for (const body of deniedBodies) {
      expect(body).not.toContain(fileName);
      expect(body).not.toContain(marker);
      expect(body).not.toContain(userText);
    }
  } finally {
    if (otherContext) await otherContext.close();
    if (ownerRegistered) {
      try {
        await apiJson(page.request, "POST", "/api/auth/logout");
      } catch {
        // The disposable browser context may already be closed after a failed assertion.
      }
    }
  }

  expect(directStoragePuts).toEqual([]);
  expect(blockedExternalRequests).toEqual([]);
});
