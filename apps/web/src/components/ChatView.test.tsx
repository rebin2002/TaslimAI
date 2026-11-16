import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const chatSource = readFileSync(new URL("./ChatView.tsx", import.meta.url), "utf8");

describe("Chat accessibility semantics", () => {
  it("uses a quiet log with a separate polite lifecycle status region", () => {
    expect(chatSource).toContain('role="log"');
    expect(chatSource).toContain('aria-live="off"');
    expect(chatSource).toContain('aria-relevant="additions text"');
    expect(chatSource).toContain('role="status" aria-live="polite" aria-atomic="true"');
  });

  it("keeps chat messages and the composer direction-aware for Arabic and Sorani text", () => {
    expect(chatSource).toContain('<article dir="auto"');
    expect(chatSource).toContain('<textarea dir="auto"');
  });

  it("announces successful regeneration completion instead of leaving generating status", () => {
    const regenerationSource = chatSource.slice(chatSource.indexOf("async function regenerate"), chatSource.indexOf("async function retryFailed"));
    expect(regenerationSource).toContain('streamEvent.type === "message.completed"');
    expect(regenerationSource).toContain('setStreamAnnouncement("completed")');
  });
});

describe("Chat attachment conversation binding", () => {
  // `history.replaceState` after a send rewrites the address bar without republishing the
  // Next.js route parameter, so `conversationId` keeps reporting the new-chat route even
  // though the composer is already inside a conversation. Uploads have to follow that
  // conversation, otherwise `StoredFileVisibility` treats a project-only chat file as
  // shared with every workspace member instead of private to the conversation.
  const uploadSource = chatSource.slice(chatSource.indexOf("async function uploadSelectedFiles"), chatSource.indexOf("async function removeAttachment"));
  const sendSource = chatSource.slice(chatSource.indexOf("async function send("), chatSource.indexOf("async function regenerate("));
  const routeEffectSource = chatSource.slice(chatSource.indexOf("invalidateChatAttachmentUploads(attachmentUploadGenerationRef);"), chatSource.indexOf("messagesEndRef.current?.scrollIntoView"));

  it("resolves the upload scope with the conversation this session created", () => {
    expect(uploadSource).toContain("resolveChatAttachmentScope(conversationId, selected, selectedProjectId, sessionConversationId)");
  });

  it("binds the session to the conversation created by a send", () => {
    const creationSource = sendSource.slice(sendSource.indexOf("api.createConversation"), sendSource.indexOf("setConversations(current => [conversation!"));
    expect(creationSource).toContain("setSessionConversationId(conversation.id)");
  });

  it("clears the session binding when the composer starts a new chat", () => {
    const newChatSource = chatSource.slice(chatSource.indexOf("function startNewChat()"), chatSource.indexOf("async function uploadSelectedFiles"));
    expect(newChatSource).toContain("setSessionConversationId(null)");
  });

  it("drops any inherited session binding when the new-chat route loads", () => {
    const newChatBranch = routeEffectSource.slice(routeEffectSource.indexOf("} else {"));
    expect(newChatBranch).toContain("setSessionConversationId(null)");
  });

  it("keeps the route conversation authoritative over the session binding", () => {
    const routeBranch = routeEffectSource.slice(routeEffectSource.indexOf("if (conversationId) {"), routeEffectSource.indexOf("} else {"));
    expect(routeBranch).toContain("setSessionConversationId(conversation.id)");
  });
});
