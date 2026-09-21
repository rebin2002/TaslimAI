export type MutableBooleanRef = { current: boolean };

export function claimSubmission(lock: MutableBooleanRef) {
  if (lock.current) return false;
  lock.current = true;
  return true;
}

export function releaseSubmission(lock: MutableBooleanRef) {
  lock.current = false;
}

export function createChatRequestId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

export type RetrySubmission = { content: string; conversationId: string; requestId: string; attachmentIds: string[] };

export function createSubmission(content: string, conversationId: string | undefined, retry?: RetrySubmission, attachmentIds: string[] = []) {
  return {
    content: retry?.content ?? content,
    conversationId: retry?.conversationId ?? conversationId,
    requestId: retry?.requestId ?? createChatRequestId(),
    attachmentIds: retry?.attachmentIds ?? attachmentIds,
  };
}

export function conversationPath(conversationId: string) {
  return `/chat/${encodeURIComponent(conversationId)}`;
}

export function shouldReplaceConversationUrl(initialConversationId: string | undefined, conversationId: string) {
  return !initialConversationId && conversationId.length > 0;
}
