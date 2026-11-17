export type ChatAttachmentScope = { projectId?: string; conversationId?: string };

type SelectedConversation = { id: string; projectId: string | null };

/**
 * The conversation a composer interaction is bound to.
 *
 * The route parameter is authoritative whenever it exists, so a conversation
 * opened from `/chat/<id>` can never be re-pointed at a different conversation.
 * On the new-chat route there is no route parameter, and a conversation that
 * merely remains in `selectedConversation` is stale by definition (#616): it
 * must not leak its private project scope into a fresh draft.
 *
 * The one trustworthy exception is `sessionConversationId`, the conversation
 * this very ChatView session created for the draft it is composing. Next.js
 * never republishes route parameters when ChatView rewrites the address bar with
 * `history.replaceState`, so after that first send the component still renders
 * the new-chat route while the user is unmistakably inside a real conversation.
 * Without this binding every follow-up upload falls back to project-only scope,
 * and `StoredFileVisibility` treats a project file with no conversation as
 * shared with the whole workspace instead of private to the conversation.
 */
export function resolveAttachmentConversationId(
  routeConversationId: string | undefined,
  sessionConversationId?: string | null,
): string | undefined {
  return routeConversationId ?? sessionConversationId ?? undefined;
}

export function resolveChatAttachmentScope(
  routeConversationId: string | undefined,
  selectedConversation: SelectedConversation | null,
  draftProjectId: string,
  sessionConversationId?: string | null,
): ChatAttachmentScope {
  const conversationId = resolveAttachmentConversationId(routeConversationId, sessionConversationId);
  const selectedMatchesConversation = conversationId !== undefined && selectedConversation?.id === conversationId;
  const projectId = conversationId
    ? (selectedMatchesConversation ? selectedConversation?.projectId ?? undefined : undefined)
    : (draftProjectId || undefined);

  return {
    ...(projectId ? { projectId } : {}),
    ...(conversationId ? { conversationId } : {}),
  };
}
