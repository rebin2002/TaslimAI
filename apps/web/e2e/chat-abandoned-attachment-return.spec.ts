import { test, expect } from "./fixtures";

// Provider-free regression-readiness coverage for abandoned composer uploads.
//
// PR #763 already owns the in-place abandonment path ("Clear all" while an upload is in
// flight) inside `apps/web/e2e/chat.spec.ts` and `ChatView.tsx`. This spec deliberately
// touches neither file and does not repeat that journey. It pins the *other* way a user
// abandons the composer: navigating away from Chat while an upload request is still in
// flight and then coming back. That path runs through a different fence in `ChatView`
// (the route-transition effect and its cleanup, not the composer clear), so it needs its
// own guard while #763 lands. No provider is called, no real user file is uploaded, and
// only synthetic in-memory fixtures are used.

const uploadUrl = /\/api\/workspaces\/[^/]+\/files$/;

test.describe("chat abandoned attachment uploads", () => {
  test("clears staged attachments when the user leaves Chat mid-upload and returns", async ({ authenticatedPage: page }) => {
    test.setTimeout(45_000);
    const attachedFileName = "e2e-return-attached.txt";
    const abandonedFileName = "e2e-return-abandoned.txt";
    let abandonedUploadStarted = false;
    let releaseAbandonedUpload: () => void = () => {};
    const abandonedUploadReleased = new Promise<void>((resolve) => {
      releaseAbandonedUpload = resolve;
    });

    // Chat uploads its queue one file at a time, so the first chip is on screen while the
    // second request is parked. That is the window leaving Chat has to cover.
    await page.route(uploadUrl, async (route) => {
      if (route.request().method() !== "POST" || !route.request().postData()?.includes(abandonedFileName)) {
        await route.continue();
        return;
      }
      abandonedUploadStarted = true;
      await abandonedUploadReleased;
      try {
        await route.fulfill({
          status: 201,
          contentType: "application/json",
          body: JSON.stringify({
            id: "00000000-0000-0000-0000-000000000301",
            workspaceId: "00000000-0000-0000-0000-000000000001",
            projectId: null,
            conversationId: null,
            originalFileName: abandonedFileName,
            contentType: "text/plain",
            extension: "txt",
            sizeBytes: 44,
            storageProvider: "Local",
            status: "Ready",
            textExtractionStatus: "NotApplicable",
            extractedTextLength: null,
            createdAt: "2026-01-01T00:00:00Z",
            processedAt: null,
          }),
        });
      } catch {
        // An abandoned upload is fenced, so the parked response may never be delivered.
      }
    });

    await page.goto("/chat");
    await page.locator('input[type="file"]').setInputFiles([
      { name: attachedFileName, mimeType: "text/plain", buffer: Buffer.from("Committed before the user left Chat.") },
      { name: abandonedFileName, mimeType: "text/plain", buffer: Buffer.from("Still in flight when the user left Chat.") },
    ]);
    await expect(page.getByText(attachedFileName)).toBeVisible();
    await expect.poll(() => abandonedUploadStarted, { timeout: 10_000 }).toBe(true);

    // The user abandons the composer by navigating to another area of the product.
    await page.getByRole("link", { name: /assets/i }).click();
    await expect(page).not.toHaveURL(/\/chat/);

    // The parked response lands only after ChatView has been torn down.
    releaseAbandonedUpload();
    await page.waitForTimeout(1_500);

    // Returning to Chat must show a fresh composer: the attachment that had been staged
    // before leaving is gone, the abandoned upload is never staged, and the abandoned
    // batch no longer locks the composer with a stale upload-progress state.
    await page.getByRole("link", { name: /chat/i }).click();
    await expect(page).toHaveURL(/\/chat/);
    await expect(page.getByText(attachedFileName)).toHaveCount(0);
    await expect(page.getByText(abandonedFileName)).toHaveCount(0);
    await expect(page.locator(".chat-upload-progress")).toHaveCount(0);
    await expect(page.getByRole("textbox", { name: /message taslim/i })).toBeEnabled();
    await expect(page.getByRole("button", { name: /attach files/i })).toBeEnabled();
  });
});
