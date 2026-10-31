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
  extension: string;
  sizeBytes: number;
  storageProvider: string;
  status: string;
  textExtractionStatus: string;
  extractedTextLength: number | null;
  createdAt: string;
  processedAt: string | null;
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
  for (const offset of offsets.slice(1)) {
    content += `${String(offset).padStart(10, "0")} 00000 n \n`;
  }
  content += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`;
  return Buffer.from(content, "ascii");
}

test("Chat PDF upload persists in CI local storage across reload and stays owner-scoped", async ({ page, testUser, browser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires the disposable Taslim CI local-storage harness.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");

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

  const chatMessagePosts: string[] = [];
  const directStoragePuts: string[] = [];
  page.on("request", request => {
    const url = new URL(request.url());
    if (request.method() === "PUT") directStoragePuts.push(`${url.origin}${url.pathname}`);
    if (request.method() === "POST" && url.pathname.startsWith("/api/conversations/") && url.pathname.includes("/messages")) {
      chatMessagePosts.push(url.pathname);
    }
  });

  await registerInUi(page, testUser);
  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const conversation = await apiJson<{ id: string; title: string }>(
    page.request,
    "POST",
    `/api/workspaces/${auth.personalWorkspace.id}/conversations`,
    { title: "E2E Local PDF Readback" },
  );
  const marker = `LOCAL_PDF_STORAGE_${randomUUID().replace(/-/g, "").slice(0, 16)}`;
  const fileName = `chat-local-${randomUUID().slice(0, 12)}.pdf`;
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
  const uploadResponseText = await uploadResponse.text();
  expect(uploadResponse.status(), uploadResponseText).toBe(201);
  const uploadRequest = uploadResponse.request();
  expect(uploadRequest.headers()["x-csrf-token"]).toBeTruthy();
  const uploadBody = uploadRequest.postData() ?? "";
  expect(uploadBody).toContain(fileName);
  expect(uploadBody).toContain(conversation.id);
  expect(uploadBody).not.toContain('name="projectId"');

  const uploaded = JSON.parse(uploadResponseText) as StoredFile;
  expect(uploaded).toMatchObject({
    workspaceId: auth.personalWorkspace.id,
    projectId: null,
    conversationId: conversation.id,
    originalFileName: fileName,
    contentType: "application/pdf",
    sizeBytes: pdfBytes.length,
    storageProvider: "Local",
    status: "Ready",
    textExtractionStatus: "Ready",
  });
  expect(uploaded.id).toBeTruthy();
  expect(uploaded.extractedTextLength).toBeGreaterThan(0);
  await expect(page.locator(".chat-attachment-chip").getByText(fileName, { exact: true })).toBeVisible();

  // Chat's pending composer chips are client state; assert durable server-side file persistence after a full page reload.
  await page.reload();
  await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();
  const listPath = `/api/workspaces/${auth.personalWorkspace.id}/files?conversationId=${conversation.id}`;
  const listedAfterReload = await apiJson<StoredFile[]>(page.request, "GET", listPath);
  expect(listedAfterReload).toHaveLength(1);
  expect(listedAfterReload[0]).toMatchObject({
    id: uploaded.id,
    workspaceId: auth.personalWorkspace.id,
    conversationId: conversation.id,
    originalFileName: fileName,
    contentType: "application/pdf",
    storageProvider: "Local",
    status: "Ready",
    textExtractionStatus: "Ready",
  });
  expect(listedAfterReload[0].extractedTextLength).toBeGreaterThan(0);

  const ownerReadback = await page.request.get(`${E2E_API_URL}/api/files/${uploaded.id}`);
  expect(ownerReadback.status()).toBe(200);
  const ownerBody = await ownerReadback.text();
  const ownerDto = JSON.parse(ownerBody) as StoredFile;
  expect(ownerDto).toMatchObject({ id: uploaded.id, conversationId: conversation.id, storageProvider: "Local", status: "Ready" });
  expect(ownerDto.extractedTextLength).toBeGreaterThan(0);
  expect(ownerBody).not.toContain(marker);

  const otherUser: TestUser = {
    displayName: "E2E PDF Privacy User",
    email: `e2e+pdf-privacy-${randomUUID().slice(0, 12)}@example.test`,
    password: testUser.password,
  };
  const otherContext = await browser.newContext({ baseURL: webUrl.origin });
  try {
    const otherPage = await otherContext.newPage();
    await otherPage.route("**/*", guardLoopbackTraffic);
    await registerInUi(otherPage, otherUser);
    const deniedFileRead = await otherPage.request.get(`${E2E_API_URL}/api/files/${uploaded.id}`);
    expect(deniedFileRead.status()).toBe(403);
    const deniedFileBody = await deniedFileRead.text();
    expect(deniedFileBody).not.toContain(fileName);
    expect(deniedFileBody).not.toContain(marker);

    const deniedConversationList = await otherPage.request.get(`${E2E_API_URL}${listPath}`);
    expect(deniedConversationList.status()).toBe(403);
    const deniedListBody = await deniedConversationList.text();
    expect(deniedListBody).not.toContain(fileName);
    expect(deniedListBody).not.toContain(marker);
  } finally {
    await otherContext.close();
  }

  expect(chatMessagePosts).toEqual([]);
  expect(directStoragePuts).toEqual([]);
  expect(blockedExternalRequests).toEqual([]);
});
