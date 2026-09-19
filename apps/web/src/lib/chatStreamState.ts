import type { ChatMessage, ChatStreamEvent } from "./api";

export type ChatStreamState = {
  messages: ChatMessage[];
  assistantId?: string;
  generating: boolean;
  terminal: "completed" | "failed" | null;
};

export function createChatStreamState(messages: ChatMessage[] = []): ChatStreamState {
  return { messages, generating: true, terminal: null };
}

function upsertMessages(messages: ChatMessage[], next: ChatMessage[]) {
  const byId = new Map(messages.map(message => [message.id, message]));
  for (const message of next) byId.set(message.id, message);
  return Array.from(byId.values());
}

export function reduceChatStream(state: ChatStreamState, event: ChatStreamEvent): ChatStreamState {
  if (event.type === "message.started" && event.data.userMessage && event.data.assistantMessage) {
    return {
      ...state,
      messages: upsertMessages(state.messages, [event.data.userMessage, event.data.assistantMessage]),
      assistantId: event.data.assistantMessage.id,
    };
  }

  if (event.type === "message.delta" && event.data.delta) {
    const targetId = event.data.messageId ?? state.assistantId;
    if (!targetId) return state;
    return {
      ...state,
      messages: state.messages.map(message => message.id === targetId
        ? { ...message, status: "Pending", content: message.content + event.data.delta }
        : message),
    };
  }

  if (event.type === "message.completed" && event.data.assistantMessage && event.data.userMessage) {
    return {
      ...state,
      messages: upsertMessages(state.messages, [event.data.userMessage, event.data.assistantMessage]),
      assistantId: event.data.assistantMessage.id,
      generating: false,
      terminal: "completed",
    };
  }

  if (event.type === "message.failed") {
    return {
      ...state,
      messages: state.assistantId
        ? state.messages.map(message => message.id === state.assistantId ? { ...message, status: "Failed" } : message)
        : state.messages,
      generating: false,
      terminal: "failed",
    };
  }

  return state;
}
