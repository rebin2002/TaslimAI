import { describe, expect, it } from "vitest";
import { beginChatAttachmentUpload, invalidateChatAttachmentUploads, isCurrentChatAttachmentUpload } from "./chatAttachmentLifecycle";

describe("chat attachment upload lifecycle", () => {
  it("invalidates an upload when the Chat route changes", () => {
    const ref = { current: 0 };
    const upload = beginChatAttachmentUpload(ref);

    expect(isCurrentChatAttachmentUpload(ref, upload)).toBe(true);
    invalidateChatAttachmentUploads(ref);
    expect(isCurrentChatAttachmentUpload(ref, upload)).toBe(false);
  });

  it("keeps a replacement upload independent from a stale upload", () => {
    const ref = { current: 0 };
    const stale = beginChatAttachmentUpload(ref);
    invalidateChatAttachmentUploads(ref);
    const current = beginChatAttachmentUpload(ref);

    expect(isCurrentChatAttachmentUpload(ref, stale)).toBe(false);
    expect(isCurrentChatAttachmentUpload(ref, current)).toBe(true);
  });
});
