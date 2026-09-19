import { describe, expect, it } from "vitest";
import {
  claimSubmission,
  conversationPath,
  createSubmission,
  releaseSubmission,
  shouldReplaceConversationUrl,
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
    });
    expect(retry.requestId).toBe(first.requestId);
    expect(retry.content).toBe(first.content);
    expect(retry.conversationId).toBe("conversation-1");
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
});
