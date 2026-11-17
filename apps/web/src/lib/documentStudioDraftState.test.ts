/**
 * Synthetic contract for the Document Studio unsaved-draft layer.
 *
 * The brief the user composes has no server-side copy until a generation job
 * is submitted, so the only thing standing between a tab switch and lost work
 * is this browser-local store. These cases pin the storage boundary (one key
 * per workspace), the fail-closed parse (a stored value that is not a readable
 * draft restores defaults, never arbitrary text), the field bounds, and the
 * tolerance for storage being unavailable. No network, no real data.
 */
import { describe, expect, it } from "vitest";
import {
  clearDocumentDraftState,
  documentDraftStorageKey,
  emptyDocumentDraftState,
  isDocumentDraftEmpty,
  parseDocumentDraftState,
  persistDocumentDraftState,
  readDocumentDraftState,
  serializeDocumentDraftState,
  type DocumentDraftState,
} from "./documentStudioState";

function memoryStorage(entries: [string, string][] = []) {
  const values = new Map<string, string>(entries);
  const reads: string[] = [];
  const writes: string[] = [];
  return {
    values,
    reads,
    writes,
    getItem(key: string) { reads.push(key); return values.get(key) ?? null; },
    setItem(key: string, value: string) { writes.push(key); values.set(key, value); },
    removeItem(key: string) { writes.push(key); values.delete(key); },
  };
}

const workspaceId = "11111111-1111-4111-8111-111111111111";
const otherWorkspaceId = "22222222-2222-4222-8222-222222222222";

const draft = (overrides: Partial<DocumentDraftState> = {}): DocumentDraftState => ({
  ...emptyDocumentDraftState(),
  prompt: "A launch brief for the retail pilot.",
  title: "Pilot launch brief",
  ...overrides,
});

describe("Document Studio unsaved draft state", () => {
  it("round-trips a composed brief through the workspace-scoped store", () => {
    const storage = memoryStorage();
    persistDocumentDraftState(workspaceId, draft(), storage);

    expect([...storage.values]).toEqual([[documentDraftStorageKey(workspaceId), expect.any(String)]] as never);
    expect(readDocumentDraftState(workspaceId, storage)).toEqual(draft());
  });

  it("reads back defaults without touching another workspace's draft", () => {
    const storage = memoryStorage();
    persistDocumentDraftState(workspaceId, draft(), storage);

    expect(readDocumentDraftState(otherWorkspaceId, storage)).toEqual(emptyDocumentDraftState());
    clearDocumentDraftState(workspaceId, storage);
    expect(readDocumentDraftState(workspaceId, storage)).toEqual(emptyDocumentDraftState());
    expect(storage.values.size).toBe(0);
  });

  it("keeps an untouched brief out of storage", () => {
    expect(isDocumentDraftEmpty(emptyDocumentDraftState())).toBe(true);
    expect(isDocumentDraftEmpty(draft())).toBe(false);
    expect(isDocumentDraftEmpty(draft({ prompt: "", title: "", outputFormat: "pdf" }))).toBe(false);

    const storage = memoryStorage();
    persistDocumentDraftState(workspaceId, emptyDocumentDraftState(), storage);
    expect(storage.values.size).toBe(0);
  });

  it("restores every guided choice, not just the free text", () => {
    const storage = memoryStorage();
    const composed = draft({
      projectId: "33333333-3333-4333-8333-333333333333",
      selected: ["44444444-4444-4444-8444-444444444444"],
      documentType: "proposal",
      length: "detailed",
      audience: "Regional retail leads",
      additionalInstructions: "Keep the pricing table.",
      language: "ar",
      outputFormat: "docx",
      tone: "persuasive",
      includeTableOfContents: false,
    });
    persistDocumentDraftState(workspaceId, composed, storage);

    expect(readDocumentDraftState(workspaceId, storage)).toEqual(composed);
  });

  it("fails closed on a stored value that is not a readable draft", () => {
    expect(parseDocumentDraftState(null)).toEqual(emptyDocumentDraftState());
    expect(parseDocumentDraftState("not-json")).toEqual(emptyDocumentDraftState());
    expect(parseDocumentDraftState("[1,2,3]")).toEqual(emptyDocumentDraftState());
    expect(parseDocumentDraftState(JSON.stringify({ prompt: 42, documentType: "sql_injection", tone: null, selected: "file-1" })))
      .toEqual(emptyDocumentDraftState());
  });

  it("drops the stored value it could not restore instead of replaying it", () => {
    const storage = memoryStorage([[documentDraftStorageKey(workspaceId), "{not-json"]]);

    expect(readDocumentDraftState(workspaceId, storage)).toEqual(emptyDocumentDraftState());
    expect(storage.values.has(documentDraftStorageKey(workspaceId))).toBe(false);
  });

  it("bounds restored text and selections to what the editor can hold", () => {
    const oversized = parseDocumentDraftState(JSON.stringify({
      prompt: "x".repeat(20_000),
      title: "t".repeat(500),
      audience: "a".repeat(900),
      additionalInstructions: "i".repeat(9_000),
      selected: Array.from({ length: 40 }, (_unused, index) => `file-${index}`),
      includeTableOfContents: false,
    }));

    expect(oversized.prompt).toHaveLength(8_000);
    expect(oversized.title).toHaveLength(160);
    expect(oversized.audience).toHaveLength(400);
    expect(oversized.additionalInstructions).toHaveLength(3_000);
    expect(oversized.selected).toHaveLength(5);
    expect(oversized.includeTableOfContents).toBe(false);
  });

  it("carries the draft across a remount through one shared session store", () => {
    const storage = memoryStorage();
    // Tab away: the mounted editor mirrors its brief into the store.
    persistDocumentDraftState(workspaceId, draft(), storage);
    // Tab back: a freshly mounted editor reads the same store.
    const restored = readDocumentDraftState(workspaceId, storage);

    expect(restored.prompt).toBe("A launch brief for the retail pilot.");
    expect(serializeDocumentDraftState(restored)).toBe(serializeDocumentDraftState(draft()));
  });

  it("stays usable when browser storage is unavailable", () => {
    expect(readDocumentDraftState(workspaceId, undefined)).toEqual(emptyDocumentDraftState());
    expect(() => persistDocumentDraftState(workspaceId, draft(), undefined)).not.toThrow();
    expect(() => clearDocumentDraftState(workspaceId, undefined)).not.toThrow();

    const throwing = {
      getItem() { throw new Error("SecurityError"); },
      setItem() { throw new Error("QuotaExceededError"); },
      removeItem() { throw new Error("SecurityError"); },
    };
    expect(readDocumentDraftState(workspaceId, throwing)).toEqual(emptyDocumentDraftState());
    expect(() => persistDocumentDraftState(workspaceId, draft(), throwing)).not.toThrow();
    expect(() => clearDocumentDraftState(workspaceId, throwing)).not.toThrow();
  });

  it("refuses to store a draft without a workspace to own it", () => {
    const storage = memoryStorage();
    persistDocumentDraftState("", draft(), storage);
    expect(storage.values.size).toBe(0);
    expect(readDocumentDraftState("", storage)).toEqual(emptyDocumentDraftState());
  });
});
