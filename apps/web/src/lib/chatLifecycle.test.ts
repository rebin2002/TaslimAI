import { describe, expect, it } from "vitest";
import {
  claimSubmission,
  conversationPath,
  createRegenerateRetryRequest,
  createSendRetryRequest,
  createSubmission,
  isAbortError,
  releaseSubmission,
  shouldReplaceConversationUrl,
  studioTransitionPath,
} from "./chatLifecycle";

describe("chat submission lifecycle", () => {
  it("allows one synchronous submission and rejects overlapping form/Enter submissions", () => {
    const lock = { current: false };
    expect(claimSubmission(lock)).toBe(true);
    expect(claimSubmission(lock)).toBe(false);
    releaseSubmission(lock);
    expect(claimSubmission(lock)).toBe(true);
  });

  it("preserves the original RequestId when retrying a failed logical submission", () => {
    const first = createSubmission("hello", "conversation-1");
    const retry = createSubmission("changed text should not win", "conversation-1", {
      content: first.content,
      conversationId: "conversation-1",
      requestId: first.requestId,
      attachmentIds: first.attachmentIds,
    });
    expect(retry.requestId).toBe(first.requestId);
    expect(retry.content).toBe(first.content);
    expect(retry.conversationId).toBe("conversation-1");
  });

  it("keeps normal sends and regenerations as separate idempotent retry operations", () => {
    const sendRetry = createSendRetryRequest("conversation-1", "hello", "send-request-1", ["file-1"]);
    const regenerateRetry = createRegenerateRetryRequest("conversation-1", "assistant-1", "regenerate-request-1");

    expect(sendRetry).toEqual({ kind: "send", conversationId: "conversation-1", content: "hello", requestId: "send-request-1", attachmentIds: ["file-1"] });
    expect(regenerateRetry).toEqual({ kind: "regenerate", conversationId: "conversation-1", messageId: "assistant-1", requestId: "regenerate-request-1" });
    expect(regenerateRetry).not.toHaveProperty("content");
  });

  it("replaces the URL only when a new conversation receives its final stream result", () => {
    expect(shouldReplaceConversationUrl(undefined, "conversation-1")).toBe(true);
    expect(shouldReplaceConversationUrl("conversation-1", "conversation-1")).toBe(false);
    expect(conversationPath("conversation/with spaces")).toBe("/chat/conversation%2Fwith%20spaces");
  });

  it("does not plan navigation for an existing conversation send", () => {
    const submission = createSubmission("follow up", "conversation-1");
    expect(shouldReplaceConversationUrl("conversation-1", submission.conversationId!)).toBe(false);
  });

  it("preserves attachments for the first message in a new conversation", () => {
    const submission = createSubmission("Summarize this PDF", undefined, undefined, ["file-pdf-1"]);

    expect(submission.conversationId).toBeUndefined();
    expect(submission.attachmentIds).toEqual(["file-pdf-1"]);

    const retry = createSubmission("ignored retry text", undefined, {
      content: submission.content,
      conversationId: "conversation-created-after-upload",
      requestId: submission.requestId,
      attachmentIds: submission.attachmentIds,
    });
    expect(retry.attachmentIds).toEqual(["file-pdf-1"]);
    expect(retry.requestId).toBe(submission.requestId);
  });

  it("creates safe project-aware transitions to existing Studios without claiming generation", () => {
    expect(studioTransitionPath("document", "project 1")).toBe("/create/document?projectId=project%201");
    expect(studioTransitionPath("research")).toBe("/create/research");
  });

  it("recognizes browser stream cancellation without treating ordinary errors as cancellation", () => {
    expect(isAbortError(new DOMException("Stopped", "AbortError"))).toBe(true);
    expect(isAbortError(new Error("network failed"))).toBe(false);
  });
});
