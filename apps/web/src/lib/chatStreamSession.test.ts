import { describe, expect, it } from "vitest";
import { beginChatStreamSession, invalidateChatStreamSessions, isCurrentChatStreamSession } from "./chatStreamSession";

describe("chat stream sessions", () => {
  it("rejects late events from an invalidated session while accepting the replacement", () => {
    const ref = { current: 0 };
    const oldSession = beginChatStreamSession(ref);

    expect(isCurrentChatStreamSession(ref, oldSession)).toBe(true);

    invalidateChatStreamSessions(ref);
    expect(isCurrentChatStreamSession(ref, oldSession)).toBe(false);

    const currentSession = beginChatStreamSession(ref);
    expect(isCurrentChatStreamSession(ref, oldSession)).toBe(false);
    expect(isCurrentChatStreamSession(ref, currentSession)).toBe(true);
  });

  it("invalidates the active session when the component is disposed", () => {
    const ref = { current: 0 };
    const session = beginChatStreamSession(ref);

    invalidateChatStreamSessions(ref);

    expect(isCurrentChatStreamSession(ref, session)).toBe(false);
  });
});
