import { randomUUID } from "node:crypto";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";

type MockStoredFile = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  conversationId: string | null;
  originalFileName: string;
  contentType: string;
  extension: string;
  sizeBytes: number;
  storageProvider: string;
  status: "Ready";
  textExtractionStatus: "NotStarted";
  extractedTextLength: null;
  createdAt: string;
  processedAt: null;
};

test("Chat PDF upload failure shows a safe error and allows a private retry without storage or inference", async ({ page, testUser }) => {
  const loopbackHosts = new Set(["localhost", "127.0.0.1", "::1"]);
  const apiUrl = new URL(E2E_API_URL);
  const webUrl = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000");
  expect(apiUrl.protocol).toBe("http:");
  expect(loopbackHosts.has(apiUrl.hostname)).toBe(true);
  expect(webUrl.protocol).toBe("http:");
  expect(loopbackHosts.has(webUrl.hostname)).toBe(true);

  const blockedExternalRequests: string[] = [];
  const allowedLocalHosts = new Set([...loopbackHosts]);
  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (route.request().method() === "POST" && /^\/api\/workspaces\/[^/]+\/files$/.test(url.pathname)) {
      blockedExternalRequests.push(`UNMOCKED FILE UPLOAD ${url.origin}${url.pathname}`);
      await route.abort("blockedbyclient");
      return;
    }
    if (allowedLocalHosts.has(url.hostname)) {
      await route.continue();
      return;
    }
    blockedExternalRequests.push(`${route.request().method()} ${url.origin}`);
    await route.abort("blockedbyclient");
  });
  await registerInUi(page, testUser);

  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const conversation = await apiJson<{ id: string; title: string }>(
    page.request,
    "POST",
    `/api/workspaces/${auth.personalWorkspace.id}/conversations`,
    { title: "E2E PDF Upload Retry Smoke" },
  );
  const fileName = "private-launch-notes.pdf";
  const fileBytes = Buffer.from("%PDF-1.4\n% Synthetic browser-only fixture; upload endpoint is intercepted.\n%%EOF\n");
  const safeStorageError = "We couldn't store this file. Please try again.";
  const uploadBodies: string[] = [];
  const chatSubmissionRequests: string[] = [];
  const directStoragePuts: string[] = [];
  const mockedStoredFile: MockStoredFile = {
    id: randomUUID(),
    workspaceId: auth.personalWorkspace.id,
    projectId: null,
    conversationId: conversation.id,
    originalFileName: fileName,
    contentType: "application/pdf",
    extension: "pdf",
    sizeBytes: fileBytes.length,
    storageProvider: "Mock",
    status: "Ready",
    textExtractionStatus: "NotStarted",
    extractedTextLength: null,
    createdAt: new Date().toISOString(),
    processedAt: null,
  };

  await page.goto(`/chat/${conversation.id}`);
  await expect(page.getByRole("heading", { name: conversation.title })).toBeVisible();

  page.on("request", (request) => {
    const url = new URL(request.url());
    if (request.method() === "PUT") directStoragePuts.push(url.origin + url.pathname);
    if (request.method() === "POST" && url.pathname.startsWith(`/api/conversations/${conversation.id}/messages`)) {
      chatSubmissionRequests.push(url.pathname);
    }
  });

  await page.route("**/api/workspaces/*/files", async (route) => {
    if (route.request().method() !== "POST") {
      await route.continue();
      return;
    }

    const requestUrl = new URL(route.request().url());
    const body = route.request().postData() ?? "";
    uploadBodies.push(body);
    expect(requestUrl.hostname).toBe(new URL(E2E_API_URL).hostname);
    expect(requestUrl.pathname).toBe(`/api/workspaces/${auth.personalWorkspace.id}/files`);
    expect(route.request().headers()["x-csrf-token"]).toBeTruthy();
    expect(body).toContain(fileName);
    expect(body).toContain('name="conversationId"');
    expect(body).toContain(conversation.id);
    expect(body).not.toContain('name="projectId"');

    if (uploadBodies.length === 1) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: { code: "FILE_STORAGE_OPERATION_FAILED", message: safeStorageError } }),
      });
      return;
    }

    if (uploadBodies.length === 2) {
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify(mockedStoredFile) });
      return;
    }

    await route.fulfill({ status: 500, contentType: "application/json", body: "Unexpected extra upload attempt" });
  });

  const pdfInput = page.locator('.chat-composer input[type="file"]');
  await pdfInput.setInputFiles({ name: fileName, mimeType: "application/pdf", buffer: fileBytes });

  const errorAlert = page.locator(".chat-inline-error");
  await expect(errorAlert).toBeVisible();
  await expect(errorAlert).toContainText(safeStorageError);
  await expect(errorAlert).not.toContainText(fileName);
  await expect(errorAlert).not.toContainText(/bucket|object.?key|secret|api.?key|stack|exception|r2/i);
  await expect(page.locator(".chat-attachment-list")).toHaveCount(0);
  await expect(page.locator(".chat-upload-progress")).toHaveCount(0);
  expect(uploadBodies).toHaveLength(1);
  expect(chatSubmissionRequests).toEqual([]);
  expect(directStoragePuts).toEqual([]);
  expect(blockedExternalRequests).toEqual([]);

  // The component clears the file input after failure; selecting the same PDF retries upload.
  await pdfInput.setInputFiles({ name: fileName, mimeType: "application/pdf", buffer: fileBytes });
  await expect(page.locator(".chat-attachment-chip").getByText(fileName, { exact: true })).toBeVisible();
  await expect(errorAlert).toHaveCount(0);
  expect(uploadBodies).toHaveLength(2);
  expect(chatSubmissionRequests).toEqual([]);
  expect(directStoragePuts).toEqual([]);
  expect(blockedExternalRequests).toEqual([]);
});
