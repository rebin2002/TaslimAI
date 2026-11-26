import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * Wiring contract for the Chat thread switcher.
 *
 * Synthetic fixtures only: this reads the component source and asserts how it
 * binds the pure `chatThreadFocus` decisions into the sidebar. No provider,
 * network, API client, translation catalog, or user conversation data is
 * involved. Focus itself is verified through the decision module rather than a
 * browser, matching how route recovery boundaries verify `recoveryFocus.ts`.
 */
const chatSource = readFileSync(new URL("./ChatView.tsx", import.meta.url), "utf8");

/** Return the sidebar block that renders the persisted thread list. */
function historyListSource(): string {
  const start = chatSource.indexOf('<div className="chat-history"');
  expect(start, "ChatView must render a chat-history sidebar block").toBeGreaterThan(-1);
  const end = chatSource.indexOf('<div className="chat-sidebar-footer"', start);
  expect(end, "the chat-history block must close before the sidebar footer").toBeGreaterThan(start);
  return chatSource.slice(start, end);
}

describe("Chat thread switch keyboard focus", () => {
  it("keeps the activated thread row mounted across a thread switch", () => {
    const source = historyListSource();

    // The bug: `loading` gated the whole sidebar, so switching threads swapped a
    // populated list for the first-load skeleton. The row the user had just
    // activated unmounted, focus fell back to document.body, and reaching the
    // list again meant re-traversing the app shell.
    expect(source).toContain("{threadMount.renderThreads ?");
    expect(source).not.toMatch(/^\s*\{loading \? <div className="chat-sidebar-loading"/);
    // The skeleton has to stay available for the genuine first load.
    expect(source).toContain('className="chat-sidebar-loading"');
  });

  it("binds the mount decision to the rendered thread count, not the route id", () => {
    expect(chatSource).toMatch(
      /const threadMount = resolveChatThreadListMount\((?:filteredConversations|groups|conversations)\.length, loading\);/,
    );
    // A thread switch changes conversationId, so keying the decision on it would
    // re-render a different list and lose the element that owns focus.
    expect(chatSource).not.toMatch(/resolveChatThreadListMount\([^)]*conversationId/);
  });

  it("reports progress on the surviving list instead of replacing it", () => {
    const source = historyListSource();
    expect(source).toContain("aria-busy={threadMount.revalidating}");
  });

  it("gives the thread list a landmark the accessible name can attach to", () => {
    const source = historyListSource();

    // aria-label on a plain <div> resolves to role="generic", which assistive
    // tech drops, so the list was announced as an unnamed group and a switch
    // left the user with nothing to orient by.
    expect(source).toContain('role="region"');
    expect(source).toContain('aria-label={t("chat.history")}');
  });

  it("exposes which thread is current instead of relying on styling alone", () => {
    const source = historyListSource();

    // `is-active` is a CSS class. Selection has to reach the accessibility tree
    // so a switch is perceivable without sight.
    expect(source).toContain("aria-current={chatThreadAriaCurrent(selected?.id, item.id)}");
    // The decision is shared, not inlined, so it stays unit-testable.
    expect(chatSource).toContain('from "@/lib/chatThreadFocus"');
  });

  it("keeps a revalidating list switchable", () => {
    const source = historyListSource();

    // Only an in-flight generation owns the composer and may block a switch.
    // Re-adding `loading` would leave the list inert while it is on screen.
    expect(source).toContain("disabled={generating}");
    expect(source).not.toContain("disabled={generating || loading}");
    expect(source).not.toContain("disabled={threadMount.revalidating}");
  });

  it("decides focus from ids only, never from conversation content", () => {
    const source = historyListSource();

    // Titles are localized and user-authored, so they must not reach the
    // accessibility attributes that describe the selection.
    expect(source).not.toMatch(/aria-current=\{[^}]*item\.title/);
    expect(source).not.toMatch(/aria-busy=\{[^}]*item\.title/);
    expect(source).not.toMatch(/aria-busy=\{[^}]*selected\?\.title/);
  });
});
