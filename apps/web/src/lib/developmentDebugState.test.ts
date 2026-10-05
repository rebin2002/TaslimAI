import { describe, expect, it } from "vitest";
import {
  DEVELOPMENT_DEBUG_MAX_NOTES,
  clearDevelopmentDebugState,
  developmentDebugProgress,
  developmentDebugStorageKey,
  emptyDevelopmentDebugState,
  loadDevelopmentDebugState,
  parseDevelopmentDebugState,
  persistDevelopmentDebugState,
  serializeDevelopmentDebugState,
} from "./developmentDebugState";

function storage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
}

describe("development debug state", () => {
  it("starts with five incomplete steps and a zero progress count", () => {
    const state = emptyDevelopmentDebugState();
    expect(developmentDebugProgress(state)).toEqual({ completed: 0, total: 5 });
  });

  it("recovers from malformed or untrusted browser data", () => {
    expect(parseDevelopmentDebugState("not-json")).toEqual(emptyDevelopmentDebugState());
    expect(parseDevelopmentDebugState(JSON.stringify({ completed: { reproduce: true, unknown: true }, notes: 42 }))).toEqual({
      completed: { reproduce: true, compare: false, boundary: false, observe: false, record: false },
      notes: "",
    });
  });

  it("bounds notes and serializes only the supported checklist shape", () => {
    const longNotes = "x".repeat(DEVELOPMENT_DEBUG_MAX_NOTES + 20);
    const state = parseDevelopmentDebugState(JSON.stringify({ completed: { reproduce: true, compare: "yes" }, notes: longNotes, extra: "private" }));
    expect(state.notes).toHaveLength(DEVELOPMENT_DEBUG_MAX_NOTES);
    expect(JSON.parse(serializeDevelopmentDebugState(state))).toEqual({
      completed: { reproduce: true, compare: false, boundary: false, observe: false, record: false },
      notes: "x".repeat(DEVELOPMENT_DEBUG_MAX_NOTES),
    });
  });

  it("isolates persistence by workspace and supports clearing one workspace", () => {
    const browserStorage = storage();
    const first = emptyDevelopmentDebugState();
    first.completed.reproduce = true;
    persistDevelopmentDebugState("workspace-a", first, browserStorage);
    const second = emptyDevelopmentDebugState();
    second.completed.record = true;
    persistDevelopmentDebugState("workspace-b", second, browserStorage);

    expect(developmentDebugStorageKey("workspace-a")).not.toBe(developmentDebugStorageKey("workspace-b"));
    expect(loadDevelopmentDebugState("workspace-a", browserStorage).completed.reproduce).toBe(true);
    expect(loadDevelopmentDebugState("workspace-a", browserStorage).completed.record).toBe(false);
    expect(loadDevelopmentDebugState("workspace-b", browserStorage).completed.record).toBe(true);

    clearDevelopmentDebugState("workspace-a", browserStorage);
    expect(loadDevelopmentDebugState("workspace-a", browserStorage)).toEqual(emptyDevelopmentDebugState());
    expect(loadDevelopmentDebugState("workspace-b", browserStorage).completed.record).toBe(true);
  });
});
