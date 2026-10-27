export type ChatAttachmentScope = { projectId?: string; conversationId?: string };

type SelectedConversation = { id: string; projectId: string | null };

export function resolveChatAttachmentScope(
  routeConversationId: string | undefined,
  selectedConversation: SelectedConversation | null,
  draftProjectId: string,
): ChatAttachmentScope {
  const selectedMatchesRoute = routeConversationId !== undefined && selectedConversation?.id === routeConversationId;
  const projectId = routeConversationId
    ? (selectedMatchesRoute ? selectedConversation?.projectId ?? undefined : undefined)
    : (draftProjectId || undefined);

  return {
    ...(projectId ? { projectId } : {}),
    ...(routeConversationId ? { conversationId: routeConversationId } : {}),
  };
}
