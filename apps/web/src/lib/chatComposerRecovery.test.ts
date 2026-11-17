import { describe, expect, it, vi } from "vitest";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";
import { chatComposerFailureDescriptionId, resolveChatComposerFailure, type ChatComposerFailure } from "@/lib/chatComposerRecovery";
import type { ChatRetryRequest } from "@/lib/chatLifecycle";

/**
 * Synthetic fixtures only: fabricated ids and presence flags. No provider,
 * network, API client, translation catalog, or user conversation data is
 * involved, so these decisions stay verifiable without a browser.
 */
const sendRetry: ChatRetryRequest = {
  kind: "send",
  conversationId: "11111111-1111-4111-8111-111111111111",
  content: "synthetic draft",
  requestId: "req-synthetic-1",
  attachmentIds: [],
};
const regenerateRetry: ChatRetryRequest = {
  kind: "regenerate",
  conversationId: "22222222-2222-4222-8222-222222222222",
  messageId: "33333333-3333-4333-8333-333333333333",
  requestId: "req-synthetic-2",
};

describe("Chat composer failure decisions", () => {
  it("renders the failure alert only for a conversation that reported an error", () => {
    // A load or admission error on the new-chat route owns the empty state
    // instead, and a composer without an error renders no banner at all.
    expect(resolveChatComposerFailure("boom", null, false)).toEqual({ alertVisible: false, retryOffered: false });
    expect(resolveChatComposerFailure("", null, true)).toEqual({ alertVisible: false, retryOffered: false });
    expect(resolveChatComposerFailure("boom", null, true)).toEqual({ alertVisible: true, retryOffered: false });
  });

  it("offers the retry action only when a retryable request is attached", () => {
    for (const retry of [sendRetry, regenerateRetry]) {
      expect(resolveChatComposerFailure("boom", retry, true).retryOffered).toBe(true);
    }
    // Composer errors without a retryable request (a failed copy or rename)
    // must not assert retry semantics.
    expect(resolveChatComposerFailure("boom", null, true).retryOffered).toBe(false);
  });

  it("decides from presence flags only, never from the failure content", () => {
    // Error text can quote a provider message or a user draft; the decision
    // and the description id it produces must not depend on it.
    const plain = resolveChatComposerFailure("The AI provider is not available.", sendRetry, true);
    const quoted = resolveChatComposerFailure('draft said "hello"', regenerateRetry, true);
    expect(plain.retryOffered).toBe(true);
    expect(quoted.retryOffered).toBe(true);
    expect(chatComposerFailureDescriptionId(plain)).toBe(chatComposerFailureDescriptionId(quoted));
    expect(chatComposerFailureDescriptionId(plain)).toBe("chat-error-message");
  });

  it("omits the editor failure description when retry is not pending", () => {
    // React must drop the attribute rather than reference an unmounted id.
    expect(chatComposerFailureDescriptionId({ alertVisible: true, retryOffered: false })).toBeUndefined();
    expect(chatComposerFailureDescriptionId({ alertVisible: false, retryOffered: false })).toBeUndefined();
  });

  it("routes only the retry state through the shared recovery focus decision", () => {
    // Composition contract: the alert panel is a recovery panel for
    // `recoveryFocus.ts`, but the composer calls it only when the failed
    // request owns the retry action. A copy/rename error leaves the user's
    // focus on the control they are on.
    const focus = vi.fn();
    const panel = { contains: () => false, focus };
    const restoreFor = (failure: ChatComposerFailure) => failure.retryOffered && restoreRecoveryFocus(panel, null);
    expect(restoreFor(resolveChatComposerFailure("boom", sendRetry, true))).toBe(true);
    expect(restoreFor(resolveChatComposerFailure("boom", null, true))).toBe(false);
    expect(restoreFor(resolveChatComposerFailure("boom", sendRetry, false))).toBe(false);
    expect(focus).toHaveBeenCalledTimes(1);
  });
});
