import { describe, expect, it } from "vitest";
import type { ChatMessage, ChatStreamData, ChatStreamEvent } from "./api";
import { createSseParser, SseProtocolError } from "./sse";
import { applyChatStreamEvent, createChatStreamState, reduceChatStream, type ChatStreamState } from "./chatStreamState";
import { beginChatStreamSession, isCurrentChatStreamSession, type ChatStreamGenerationRef } from "./chatStreamSession";

// #lane regen-superseded-context — the consumer-facing half nobody pinned.
//
// Pressing Regenerate twice in a row sends two
// POST /api/conversations/{id}/messages/{messageId}/regenerate requests for the same
// conversation and the same source user turn. Nothing serialises them: ChatView starts a
// new stream session for the second click but never aborts the first fetch, so the first
// response can land — wholly or in pieces — AFTER the second request has been accepted.
// Whatever that late response carries, text chunks, a citation-shaped frame, or the
// terminal frame that settles the turn together with its usage-derived flag, it belongs to
// a turn the user already replaced and must not reach the newer turn.
//
// docs/CHAT_ARCHITECTURE.md already promises the superseded response is kept out of the
// model context: #998 pins that server-side for the JSON path and #1171 for the stream
// path. No test pinned the other half — what the person watching the screen may see.
//
// This file drives the real pieces in exactly the composition ChatView uses: the frame
// format ChatController.WriteEventAsync emits, one real SSE parser per live request, the
// real stream-session gate, the real reducer. Synthetic fixtures only: no network, no
// provider, no billing, no shared test file.

/** Byte-for-byte the frame shape of ChatController.WriteEventAsync. */
function sseFrame(type: string, data: unknown): string {
  return `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`;
}

function message(id: string, role: ChatMessage["role"], content: string, status: ChatMessage["status"], sequence: number, isTestResponse?: boolean): ChatMessage {
  return { id, conversationId: "conversation-1", role, content, status, createdAt: "2026-01-01T00:00:00Z", sequence, isTestResponse };
}

// Regenerate reuses the source user turn and appends its own assistant row, so the two
// competing turns are distinguishable only by assistant id.
const sourceUser = message("user-1", "User", "Summarise the trade route", "Completed", 1);
const firstAssistant = message("assistant-first", "Assistant", "", "Pending", 2);
const secondAssistant = message("assistant-second", "Assistant", "", "Pending", 3);

const FIRST_CHUNKS = ["FIRST-LATE-CHUNK-A", "FIRST-LATE-CHUNK-B"];
const FIRST_SETTLED = FIRST_CHUNKS.join("");
const SECOND_CHUNK = "SECOND-CHUNK";

const firstStarted = sseFrame("message.started", { userMessage: sourceUser, assistantMessage: firstAssistant });
const firstDelta = (chunk: string) => sseFrame("message.delta", { messageId: firstAssistant.id, delta: chunk });
// ToMessageDto(assistant, usage.IsTestResponse): the settled DTO plus the usage-derived
// flag is this protocol's terminal-usage surface.
const firstCompleted = sseFrame("message.completed", {
  userMessage: sourceUser,
  assistantMessage: message(firstAssistant.id, "Assistant", FIRST_SETTLED, "Completed", firstAssistant.sequence, true),
});
const firstFailed = sseFrame("message.failed", { code: "AI_GENERATION_FAILED", message: "late failure of the superseded turn" });

/** The complete first response, as it lands when it is late from the very first byte. */
const wholeFirstResponse = [firstStarted, ...FIRST_CHUNKS.map(firstDelta), firstCompleted].join("");
/** Its tail only, for a response whose started frame was consumed before the second click. */
const firstResponseTail = [...FIRST_CHUNKS.map(firstDelta), firstCompleted].join("");

const secondStarted = sseFrame("message.started", { userMessage: sourceUser, assistantMessage: secondAssistant });
const secondDelta = sseFrame("message.delta", { messageId: secondAssistant.id, delta: SECOND_CHUNK });
const secondCompleted = sseFrame("message.completed", {
  userMessage: sourceUser,
  assistantMessage: message(secondAssistant.id, "Assistant", SECOND_CHUNK, "Completed", secondAssistant.sequence),
});

/** Every frame the newer turn is allowed to show, for leak assertions. */
function visibleText(state: ChatStreamState): string {
  return state.messages.map(item => `${item.id}:${item.status}:${item.content}`).join("|");
}

/**
 * The ChatView stream seam without React: one visible store fed by applyStreamEvent's
 * onApplied callback (setMessages + setGenerating), and one callback per click wrapped in
 * that click's isCurrentStream() gate, each owning one live parser as streamRequest does.
 * `gated: false` removes the session check, which is what the negative control needs.
 */
function createHarness(gated = true) {
  const generation: ChatStreamGenerationRef = { current: 0 };
  let state = createChatStreamState([sourceUser]);
  const renders: ChatStreamState[] = [];

  function openStream() {
    const session = beginChatStreamSession(generation);
    let dispatched = 0;
    const deliver = (event: ChatStreamEvent) => {
      if (gated && !isCurrentChatStreamSession(generation, session)) return;
      dispatched += 1;
      state = applyChatStreamEvent(state, event, next => renders.push(next));
    };
    const parser = createSseParser<ChatStreamData>(deliver);
    return {
      /** Fragmented arrival, the way a late response really lands. */
      push(bytes: string) {
        for (let offset = 0; offset < bytes.length; offset += 7) parser.push(bytes.slice(offset, offset + 7));
      },
      /** Only a stream that reached a terminal event may be closed. */
      finish() {
        parser.end();
      },
      feed(event: ChatStreamEvent) {
        deliver(event);
      },
      dispatchedCount: () => dispatched,
    };
  }

  return {
    openStream,
    view: () => state,
    row: (id: string) => state.messages.find(item => item.id === id),
    renderedText: () => renders.map(visibleText).join("||"),
    renderCount: () => renders.length,
  };
}

type Stream = ReturnType<ReturnType<typeof createHarness>["openStream"]>;

/** Click 1 accepted, click 2 accepted and streaming, and only then does click 1 answer. */
function acceptTwoRegenerationsWithLateFirstResponse(harness: ReturnType<typeof createHarness>): { first: Stream; second: Stream } {
  const first = harness.openStream();
  const second = harness.openStream();
  second.push(secondStarted);
  second.push(secondDelta);
  expect(harness.view()).toMatchObject({ generating: true, terminal: null, assistantId: secondAssistant.id });
  expect(harness.row(secondAssistant.id)).toMatchObject({ content: SECOND_CHUNK, status: "Pending" });
  return { first, second };
}

describe("superseded regenerate stream isolation (first SSE response arriving late)", () => {
  it("keeps a whole late first response — chunks, terminal frame and usage flag — out of the newer turn", () => {
    const harness = createHarness();
    const { first, second } = acceptTwoRegenerationsWithLateFirstResponse(harness);
    const rendersBefore = harness.renderCount();

    first.push(wholeFirstResponse);

    // Not one frame of the superseded response crossed the session boundary.
    expect(first.dispatchedCount()).toBe(0);
    expect(harness.renderCount()).toBe(rendersBefore);
    expect(harness.row(firstAssistant.id)).toBeUndefined();
    expect(harness.renderedText()).not.toMatch(/FIRST-LATE-CHUNK/);

    // The newer turn is untouched: still generating, its own text only, no usage flag.
    expect(harness.view()).toMatchObject({ generating: true, terminal: null, assistantId: secondAssistant.id });
    expect(harness.row(secondAssistant.id)).toMatchObject({ content: SECOND_CHUNK, status: "Pending" });
    expect(harness.row(secondAssistant.id)?.isTestResponse).toBeUndefined();

    // And it still settles from its own stream alone.
    second.push(secondCompleted);
    expect(() => second.finish()).not.toThrow();
    expect(harness.view()).toMatchObject({ generating: false, terminal: "completed", assistantId: secondAssistant.id });
    expect(harness.row(secondAssistant.id)).toMatchObject({ content: SECOND_CHUNK, status: "Completed" });
    expect(harness.row(firstAssistant.id)).toBeUndefined();
  });

  it("keeps the late tail of a response whose started frame was already shown out of the newer turn", () => {
    const harness = createHarness();
    const first = harness.openStream();
    first.push(firstStarted); // the first turn is visible while it is still the newest one
    const second = harness.openStream();
    second.push(secondStarted);
    second.push(secondDelta);
    const rendersBefore = harness.renderCount();

    first.push(firstResponseTail);

    expect(first.dispatchedCount()).toBe(1); // its own message.started only
    expect(harness.renderCount()).toBe(rendersBefore);
    expect(harness.row(firstAssistant.id)).toMatchObject({ content: "", status: "Pending" });
    expect(harness.renderedText()).not.toContain(FIRST_SETTLED);
    expect(harness.view()).toMatchObject({ generating: true, terminal: null, assistantId: secondAssistant.id });
  });

  it("keeps a lone late terminal failure of the superseded turn from failing the newer turn", () => {
    const harness = createHarness();
    const { first, second } = acceptTwoRegenerationsWithLateFirstResponse(harness);
    first.push(firstStarted);

    // message.failed carries no assistant id, so the session gate is the only thing
    // standing between a replaced turn's failure and the turn now on screen.
    expect(() => first.push(firstFailed)).not.toThrow();
    expect(first.dispatchedCount()).toBe(0);
    expect(harness.row(secondAssistant.id)).toMatchObject({ content: SECOND_CHUNK, status: "Pending" });
    expect(harness.view()).toMatchObject({ generating: true, terminal: null });

    second.push(secondCompleted);
    second.finish();
    expect(harness.row(secondAssistant.id)).toMatchObject({ status: "Completed" });
  });

  it("keeps a citation-shaped frame of the superseded turn out of the newer turn", () => {
    const harness = createHarness();
    const { first } = acceptTwoRegenerationsWithLateFirstResponse(harness);
    const rendersBefore = harness.renderCount();

    // A frame the protocol does not define is rejected inside the superseded stream's own
    // transport, so it can never be delivered to the newer turn.
    expect(() => first.push(sseFrame("citation.delta", { messageId: firstAssistant.id, citationId: "cite-1" }))).toThrow(SseProtocolError);
    expect(harness.renderCount()).toBe(rendersBefore);
    expect(harness.view()).toMatchObject({ generating: true, terminal: null });

    // And even handed straight to the reducer, an unknown frame cannot mutate the newer
    // turn: the state object comes back identical.
    const before = harness.view();
    const after = reduceChatStream(before, { type: "citation.delta", data: { messageId: firstAssistant.id } } as unknown as ChatStreamEvent);
    expect(after).toBe(before);
    expect(after.messages.find(item => item.id === secondAssistant.id)?.content).toBe(SECOND_CHUNK);
  });

  it("negative control: without the session gate the same late response does clobber the newer turn", () => {
    // The legs above pin the stream-session gate, not the reducer's per-messageId scoping.
    // Replaying the identical bytes with the gate removed shows the failure mode.
    const ungated = createHarness(false);
    const { first } = acceptTwoRegenerationsWithLateFirstResponse(ungated);

    first.push(wholeFirstResponse);

    // The superseded answer rebinds the store, lands its own text, and terminates the
    // newer turn's generation with the superseded turn's usage-derived flag.
    expect(ungated.view()).toMatchObject({ generating: false, terminal: "completed", assistantId: firstAssistant.id });
    expect(ungated.row(firstAssistant.id)).toMatchObject({ content: FIRST_SETTLED, status: "Completed", isTestResponse: true });
    expect(ungated.row(secondAssistant.id)).toMatchObject({ status: "Pending" });
    expect(ungated.renderedText()).toContain(FIRST_SETTLED);

    // A lone late failure frame behaves the same way: message.failed carries no assistant
    // id, so the newer row is flipped from Pending to Failed.
    const failedOnly = createHarness(false);
    const { first: lateTurn } = acceptTwoRegenerationsWithLateFirstResponse(failedOnly);
    lateTurn.feed({ type: "message.failed", data: { code: "AI_GENERATION_FAILED", message: "late failure" } });
    expect(failedOnly.row(secondAssistant.id)).toMatchObject({ status: "Failed" });
    expect(failedOnly.view()).toMatchObject({ generating: false, terminal: "failed" });
  });
});
