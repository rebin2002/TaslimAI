/**
 * Focus and selection decisions for the Chat thread switcher.
 *
 * Switching a Chat thread is a route transition: ChatView's route effect raises
 * `loading` for the whole view before the next conversation resolves. The
 * sidebar used to answer that flag by swapping the persisted thread list for the
 * first-load skeleton, which unmounts the `.chat-history-item` button the user
 * had just activated. Focus then falls back to `document.body`, so a keyboard or
 * screen-reader user has to re-traverse the app shell to reach the list again,
 * and the thread that is now on screen is only marked by a CSS class that
 * assistive tech cannot read.
 *
 * This module owns both decisions so they can be verified without a browser or
 * a real navigation, the same way route recovery boundaries verify
 * `recoveryFocus.ts`.
 */

export type ChatThreadListMount = {
  /**
   * True when the sidebar has to keep rendering the persisted thread list.
   * Only a first load, which has nothing rendered yet, may replace it with the
   * skeleton.
   */
  renderThreads: boolean;
  /** True when the rendered list is refreshing in place and should say so. */
  revalidating: boolean;
};

/**
 * Decide how the sidebar renders while a Chat route transition is loading.
 *
 * A list that already holds threads is preserved so the activated row keeps the
 * element that owns focus; the row itself is keyed by conversation id, so React
 * reuses the node across the switch. Progress is reported on the surviving list
 * instead of stealing focus from it.
 */
export function resolveChatThreadListMount(
  renderedThreadCount: number,
  loading: boolean,
): ChatThreadListMount {
  const revalidating = loading && renderedThreadCount > 0;
  return { renderThreads: !loading || revalidating, revalidating };
}

/**
 * The `aria-current` value for a thread row.
 *
 * A thread switch changes which conversation is on screen, so the row that
 * represents it has to be identifiable to assistive tech rather than only
 * styled. Non-current rows return `undefined`, which React omits, so the
 * attribute is never asserted falsely.
 */
export function chatThreadAriaCurrent(
  selectedConversationId: string | null | undefined,
  conversationId: string,
): "true" | undefined {
  if (!selectedConversationId) return undefined;
  return selectedConversationId === conversationId ? "true" : undefined;
}
