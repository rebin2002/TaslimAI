import { test, expect, apiJson, createProject } from "./fixtures";

// Provider-free coverage for the attachment scope of a conversation that ChatView
// creates itself (#623 scope ownership).
//
// `apps/web/e2e/chat.spec.ts` pins the project handoff of a new-draft upload and the
// composer abandonment fences, and `chat-abandoned-attachment-return.spec.ts` (#786)
// pins the leave-and-return cleanup. Neither covers what happens *after* the first
// message of a new conversation is sent. ChatView rewrites the address bar with
// `history.replaceState`, which does not republish the Next.js route parameter, so the
// mounted view keeps rendering the new-chat route while the user is plainly inside a
// real conversation. Every follow-up upload therefore dropped its conversation binding
// and was stored as a project-only file, and `StoredFileVisibility` treats a project
// file with no conversation as visible to every workspace member rather than private to
// its uploader. Reloading the same screen produced the opposite, correct scope, which
// is what makes this a browser-state defect rather than a display glitch.
//
// Uploads are real: they reach the composed API running with
// `Files__StorageProvider=Local`, so the stored scope is server-validated instead of
// assumed. Only the assistant reply is stubbed, which keeps the journey provider-free.
// No paid provider is called, no customer data is touched, and the fixtures create a
// throwaway signed-up user with synthetic files.
const uploadPattern = /\/api\/workspaces\/[^/]+\/files$/;
const draftFileName = "scope-new-draft.txt";
const followUpFileName = "scope-follow-up.txt";
const reloadedFileName = "scope-after-reload.txt";
type StoredFileRecord = { id: string; originalFileName: string; projectId: string | null; conversationId: string | null };
type UploadObservation = { form: string; record: StoredFileRecord | null };
/**
 * Read a scalar field out of the raw multipart body the browser actually sent, so the
 * assertion covers the wire contract rather than the resolver's return value.
 */
function multipartField(body: string, field: string): string | null {
  const match = body.match(new RegExp(`name="${field}"\\r?\\n\\r?\\n([^\\r]*)`));
  return match ? match[1].trim() : null;
}
test.describe("chat new-conversation attachment scope", () => {
  test("keeps follow-up attachments bound to the conversation created from the new-chat route", async ({ authenticatedPage: page }) => {
    test.setTimeout(90_000);
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const workspaceId = auth.personalWorkspace.id;
    const project = await createProject(page, "E2E New Conversation Scope Project");
    const uploads = new Map<string, UploadObservation>();
    page.on("response", async (response) => {
      const request = response.request();
      if (request.method() !== "POST" || !uploadPattern.test(request.url())) return;
      const form = request.postData() ?? "";
      for (const fileName of [draftFileName, followUpFileName, reloadedFileName]) {
        if (!form.includes(fileName)) continue;
        uploads.set(fileName, { form, record: await response.json().catch(() => null) as StoredFileRecord | null });
      }
    });
    // Capture the conversation ChatView creates so the stubbed reply can carry its real,
    // server-issued identity. The id is not invented here: the API validates it against
    // the workspace, the uploader and the project on every later upload.
    let createdConversation: { id: string; workspaceId: string; projectId?: string | null } | null = null;
    await page.route("**/api/workspaces/*/conversations", async (route) => {
      if (route.request().method() !== "POST") {
        await route.continue();
        return;
      }
      const response = await route.fetch();
      createdConversation = await response.json() as { id: string; workspaceId: string; projectId?: string | null };
      await route.fulfill({ response });
    });
    await page.route("**/api/conversations/*/messages/stream", async (route) => {
      const conversation = createdConversation;
      if (!conversation) {
        await route.abort();
        return;
      }
      const createdAt = new Date().toISOString();
      const userMessage = { id: "e2e-scope-user-message", conversationId: conversation.id, role: "User", content: "E2E scope probe", status: "Completed", createdAt, sequence: 1 };
      const assistantMessage = { id: "e2e-scope-assistant-message", conversationId: conversation.id, role: "Assistant", content: "", status: "Pending", createdAt, sequence: 2 };
      const completedAssistant = { ...assistantMessage, status: "Completed", content: "Deterministic scoped response." };
      await route.fulfill({
        status: 200,
        contentType: "text/event-stream",
        body: [
          "event: message.started",
          `data: ${JSON.stringify({ conversation, userMessage, assistantMessage })}`,
          "",
          "event: message.completed",
          `data: ${JSON.stringify({ conversation, userMessage, assistantMessage: completedAssistant })}`,
          "",
        ].join("\n"),
      });
    });
    await page.goto(`/chat?projectId=${project.id}`);
    await expect(page.getByLabel("Project context")).toHaveValue(project.id);
    // 1. A fresh draft has no conversation, so project-only scope is the correct answer.
    //    This also pins that the fix does not over-bind a draft that owns nothing.
    await page.locator('input[type="file"]').setInputFiles({ name: draftFileName, mimeType: "text/plain", buffer: Buffer.from("Draft context created before any conversation exists.") });
    await expect(page.getByText(draftFileName)).toBeVisible();
    await expect.poll(() => uploads.get(draftFileName)?.record, { timeout: 20_000 }).toBeTruthy();
    const draftUpload = uploads.get(draftFileName)!;
    expect(multipartField(draftUpload.form, "projectId")).toBe(project.id);
    expect(multipartField(draftUpload.form, "conversationId")).toBeNull();
    expect(draftUpload.record?.projectId).toBe(project.id);
    expect(draftUpload.record?.conversationId).toBeNull();
    // 2. The first message makes ChatView create the conversation. The address bar is
    //    rewritten to the conversation route while the mounted view stays on /chat.
    await page.getByLabel("Message Taslim...").fill("E2E scope probe");
    await page.getByRole("button", { name: /send message/i }).click();
    await expect(page.getByText("Deterministic scoped response.")).toBeVisible({ timeout: 20_000 });
    await expect.poll(() => createdConversation, { timeout: 10_000 }).toBeTruthy();
    const conversationId = createdConversation!.id;
    await expect(page).toHaveURL(new RegExp(`/chat/${conversationId}$`));
    // 3. The follow-up upload has to follow the conversation the composer is bound to.
    //    Before the fix this form carried `projectId` only.
    await page.locator('input[type="file"]').setInputFiles({ name: followUpFileName, mimeType: "text/plain", buffer: Buffer.from("Context added inside the conversation.") });
    await expect(page.getByText(followUpFileName)).toBeVisible();
    await expect.poll(() => uploads.get(followUpFileName)?.record, { timeout: 20_000 }).toBeTruthy();
    const followUpUpload = uploads.get(followUpFileName)!;
    expect(multipartField(followUpUpload.form, "conversationId")).toBe(conversationId);
    expect(multipartField(followUpUpload.form, "projectId")).toBe(project.id);
    expect(followUpUpload.record?.conversationId).toBe(conversationId);
    expect(followUpUpload.record?.projectId).toBe(project.id);
    // 4. Server-side readback through the conversation-scoped listing, which only returns
    //    rows whose stored conversation matches.
    await expect.poll(async () => {
      const files = await apiJson<StoredFileRecord[]>(page.request, "GET", `/api/workspaces/${workspaceId}/files?conversationId=${conversationId}`);
      return files.some(file => file.originalFileName === followUpFileName && file.conversationId === conversationId);
    }, { timeout: 15_000 }).toBe(true);
    // 5. After a reload the route parameter and the session binding agree, so the scope is
    //    identical. Pre-fix, this reloaded case was the only one that behaved correctly,
    //    which is exactly the inconsistency the journey pins.
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`/chat/${conversationId}$`));
    await page.locator('input[type="file"]').setInputFiles({ name: reloadedFileName, mimeType: "text/plain", buffer: Buffer.from("Context added after reloading the conversation route.") });
    await expect(page.getByText(reloadedFileName)).toBeVisible();
    await expect.poll(() => uploads.get(reloadedFileName)?.record, { timeout: 20_000 }).toBeTruthy();
    const reloadedUpload = uploads.get(reloadedFileName)!;
    expect(multipartField(reloadedUpload.form, "conversationId")).toBe(conversationId);
    expect(reloadedUpload.record?.conversationId).toBe(conversationId);
  });
});
