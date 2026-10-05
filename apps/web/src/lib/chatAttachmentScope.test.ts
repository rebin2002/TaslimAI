import { describe, expect, it } from "vitest";
import { resolveChatAttachmentScope } from "./chatAttachmentScope";

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

  it("keeps a new conversation draft scoped to its selected project", () => {
    expect(resolveChatAttachmentScope(undefined, null, "project-2")).toEqual({ projectId: "project-2" });
  });
});
