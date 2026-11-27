import type { ChatRetryRequest } from "@/lib/chatLifecycle";

/**
 * Failure recovery decisions for the Chat composer.
 *
 * When a Chat request fails, the composer keeps the draft and the inline
 * alert below the message list offers the retry action. But the element the
 * user activated to start that request is gone by the time the alert
 * appears: the textarea is disabled while the request runs and the
 * regenerate button unmounts, so focus has already fallen back to
 * `document.body`. The retry action is then not next in tab order and the
 * failure reads as a message with no way to act on it.
 *
 * This module owns the two decisions so they can be verified without a
 * browser or a real generation, the same way `chatThreadFocus.ts` owns the
 * thread-switch decisions and `recoveryFocus.ts` owns the route-boundary
 * one. Decisions compare presence flags only: no error text, provider
 * detail or user content ever reaches the accessibility attributes.
 */

export type ChatComposerFailure = {
  /** True when the inline alert renders: it needs a conversation and an error. */
  alertVisible: boolean;
  /**
   * True when the visible alert carries the retry action. Only this failure
   * state owns keyboard focus, because only it follows an activated request
   * control. Composer errors without a retry (a failed copy or rename) must
   * not pull focus away from the control the user is on.
   */
  retryOffered: boolean;
};

/**
 * Decide what the composer's failure surface currently asserts.
 *
 * `alertVisible` mirrors the render gate of the inline alert, and
 * `retryOffered` additionally requires a retryable request, so focus
 * restoration and the editor's failure description fire together with the
 * retry action instead of on any error banner.
 */
export function resolveChatComposerFailure(
  error: string,
  retryRequest: ChatRetryRequest | null,
  hasConversation: boolean,
): ChatComposerFailure {
  const alertVisible = !!error && hasConversation;
  return { alertVisible, retryOffered: alertVisible && !!retryRequest };
}

/**
 * The id of the alert text the composer points assistive technology at while
 * a failed request still owns the draft, or `undefined` when nothing is
 * asserted. React omits an `undefined` `aria-describedby`, so the editor is
 * never left referencing a description that has unmounted.
 */
export function chatComposerFailureDescriptionId(
  failure: ChatComposerFailure,
): string | undefined {
  if (!failure.retryOffered) return undefined;
  return "chat-error-message";
}
