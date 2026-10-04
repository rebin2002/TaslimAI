export type ChatStreamGenerationRef = { current: number };

export type ChatStreamSession = { generation: number };

export function beginChatStreamSession(ref: ChatStreamGenerationRef): ChatStreamSession {
  ref.current += 1;
  return { generation: ref.current };
}

export function invalidateChatStreamSessions(ref: ChatStreamGenerationRef) {
  ref.current += 1;
}

export function isCurrentChatStreamSession(ref: ChatStreamGenerationRef, session: ChatStreamSession) {
  return ref.current === session.generation;
}
