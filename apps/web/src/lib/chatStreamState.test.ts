import { describe, expect, it } from "vitest";
import type { ChatMessage, ChatStreamEvent } from "./api";
import { createChatStreamState, reduceChatStream } from "./chatStreamState";

function message(id: string, role: ChatMessage["role"], content: string, status: ChatMessage["status"]): ChatMessage {
  return { id, conversationId: "conversation-1", role, content, status, createdAt: "2026-01-01T00:00:00Z", sequence: role === "User" ? 1 : 2 };
}

const user = message("user-1", "User", "List two numbers", "Completed");
const assistant = message("assistant-1", "Assistant", "", "Pending");

function event(type: ChatStreamEvent["type"], data: ChatStreamEvent["data"]): ChatStreamEvent {
  return { type, data };
}

describe("Chat stream state", () => {
  it("renders progressive assistant deltas and terminates on completion", () => {
    let state = createChatStreamState();
    state = reduceChatStream(state, event("message.started", { userMessage: user, assistantMessage: assistant }));
    expect(state.generating).toBe(true);
    expect(state.messages[1].content).toBe("");

    for (const [delta, expected] of [["1", "1"], [",", "1,"], [" ", "1, "], ["2", "1, 2"]]) {
      state = reduceChatStream(state, event("message.delta", { messageId: "assistant-1", delta }));
      expect(state.messages.find(item => item.id === "assistant-1")?.content).toBe(expected);
      expect(state.generating).toBe(true);
    }

    const completed = message("assistant-1", "Assistant", "1, 2", "Completed");
    state = reduceChatStream(state, event("message.completed", { conversation: undefined, userMessage: user, assistantMessage: completed }));
    expect(state.messages.find(item => item.id === "assistant-1")).toMatchObject({ content: "1, 2", status: "Completed" });
    expect(state.generating).toBe(false);
    expect(state.terminal).toBe("completed");
  });

  it("uses the started assistant ID when a delta omits messageId", () => {
    let state = reduceChatStream(createChatStreamState(), event("message.started", { userMessage: user, assistantMessage: assistant }));
    state = reduceChatStream(state, event("message.delta", { delta: "1" }));
    expect(state.messages.find(item => item.id === "assistant-1")?.content).toBe("1");
  });

  it("marks the active assistant failed and ends generation on failure", () => {
    let state = reduceChatStream(createChatStreamState(), event("message.started", { userMessage: user, assistantMessage: assistant }));
    state = reduceChatStream(state, event("message.delta", { messageId: "assistant-1", delta: "partial" }));
    state = reduceChatStream(state, event("message.failed", { code: "AI_GENERATION_FAILED", message: "safe failure" }));
    expect(state.messages.find(item => item.id === "assistant-1")).toMatchObject({ content: "partial", status: "Failed" });
    expect(state.generating).toBe(false);
    expect(state.terminal).toBe("failed");
  });
});
