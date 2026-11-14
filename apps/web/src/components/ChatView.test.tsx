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
