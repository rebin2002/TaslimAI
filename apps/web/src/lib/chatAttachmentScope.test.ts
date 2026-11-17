import { describe, expect, it } from "vitest";
import { resolveAttachmentConversationId, resolveChatAttachmentScope } from "./chatAttachmentScope";

describe("chat attachment scope", () => {
  it("binds uploads to the current route conversation once it is known", () => {
    expect(resolveChatAttachmentScope("conversation-1", { id: "conversation-1", projectId: "project-1" }, "stale-project")).toEqual({
      conversationId: "conversation-1",
      projectId: "project-1",
    });
  });

  it("does not inherit a stale selected conversation project during a route transition", () => {
    expect(resolveChatAttachmentScope("conversation-2", { id: "conversation-1", projectId: "project-1" }, "stale-project")).toEqual({
      conversationId: "conversation-2",
    });
  });

  it("does not inherit a stale selected conversation on the new-chat route", () => {
    expect(resolveChatAttachmentScope(undefined, { id: "conversation-1", projectId: "private-project" }, "project-2")).toEqual({
      projectId: "project-2",
    });
  });

  it("keeps a new conversation draft scoped to its selected project", () => {
    expect(resolveChatAttachmentScope(undefined, null, "project-2")).toEqual({ projectId: "project-2" });
  });

  describe("a conversation created from the new-chat route", () => {
    // ChatView rewrites the address bar with `history.replaceState` after it creates a
    // conversation, so the route parameter keeps reporting the new-chat route while the
    // user is already inside that conversation. Follow-up uploads have to stay bound to it.
    it("binds a follow-up upload to the conversation this session created", () => {
      expect(resolveChatAttachmentScope(undefined, { id: "conversation-1", projectId: "project-1" }, "project-1", "conversation-1")).toEqual({
        conversationId: "conversation-1",
        projectId: "project-1",
      });
    });

    it("keeps the upload conversation-private when the conversation has no project", () => {
      expect(resolveChatAttachmentScope(undefined, { id: "conversation-1", projectId: null }, "", "conversation-1")).toEqual({
        conversationId: "conversation-1",
      });
    });

    it("scopes the upload to the bound conversation's own project instead of the draft selector", () => {
      expect(resolveChatAttachmentScope(undefined, { id: "conversation-1", projectId: "project-1" }, "project-2", "conversation-1")).toEqual({
        conversationId: "conversation-1",
        projectId: "project-1",
      });
    });

    it("never replays the bound conversation's project when its identity no longer matches", () => {
      expect(resolveChatAttachmentScope(undefined, { id: "conversation-9", projectId: "project-9" }, "project-1", "conversation-1")).toEqual({
        conversationId: "conversation-1",
      });
    });
  });

  describe("conversation identity precedence", () => {
    it("prefers the route parameter over a session binding", () => {
      expect(resolveAttachmentConversationId("conversation-2", "conversation-1")).toBe("conversation-2");
    });

    it("uses the session binding only on the new-chat route", () => {
      expect(resolveAttachmentConversationId(undefined, "conversation-1")).toBe("conversation-1");
    });

    it("stays unbound when neither the route nor the session has a conversation", () => {
      expect(resolveAttachmentConversationId(undefined, null)).toBeUndefined();
      expect(resolveAttachmentConversationId(undefined)).toBeUndefined();
    });

    it("lets the route conversation win so a session binding cannot replay into another conversation", () => {
      expect(resolveChatAttachmentScope("conversation-2", { id: "conversation-1", projectId: "project-1" }, "project-1", "conversation-1")).toEqual({
        conversationId: "conversation-2",
      });
    });

    it("leaves a fresh new-chat draft project-scoped until a conversation is created", () => {
      expect(resolveChatAttachmentScope(undefined, null, "project-1", null)).toEqual({ projectId: "project-1" });
    });
  });
});
