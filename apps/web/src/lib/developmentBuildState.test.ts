import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createDefaultDevelopmentBuildState,
  developmentBuildStorageKey,
  isDevelopmentBuildStepComplete,
  readDevelopmentBuildState,
  saveDevelopmentBuildState,
  setDevelopmentBuildNote,
  setDevelopmentBuildStepCompletion,
} from "./developmentBuildState";

afterEach(() => vi.unstubAllGlobals());

describe("development build state", () => {
  it("uses a distinct durable key for each workspace", () => {
    expect(developmentBuildStorageKey("workspace-a")).not.toBe(developmentBuildStorageKey("workspace-b"));
    expect(developmentBuildStorageKey("workspace-a")).toBe("taslim:development-build:workspace-a");
  });

  it("persists checklist progress only inside the active workspace namespace", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { localStorage: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
    } });
    const completed = setDevelopmentBuildStepCompletion(createDefaultDevelopmentBuildState(), "scope", true);
    expect(saveDevelopmentBuildState("workspace-a", completed)).toBe(true);
    expect(isDevelopmentBuildStepComplete(readDevelopmentBuildState("workspace-a"), "scope")).toBe(true);
    expect(isDevelopmentBuildStepComplete(readDevelopmentBuildState("workspace-b"), "scope")).toBe(false);
  });

  it("recovers safely from malformed or untrusted stored data", () => {
    vi.stubGlobal("window", { localStorage: { getItem: () => "{not-json" } });
    expect(readDevelopmentBuildState("workspace-a")).toEqual(createDefaultDevelopmentBuildState());

    vi.stubGlobal("window", { localStorage: { getItem: () => JSON.stringify({
      version: 1,
      completedStepIds: ["one", 7, " one ", "two"],
      note: "x".repeat(3_000),
      updatedAt: "safe",
    }) } });
    const recovered = readDevelopmentBuildState("workspace-a");
    expect(recovered.completedStepIds).toEqual(["one", "two"]);
    expect(recovered.note).toHaveLength(2_000);
    expect(recovered.updatedAt).toBe("safe");
  });

  it("bounds notes and tolerates unavailable storage without throwing", () => {
    vi.stubGlobal("window", { localStorage: { setItem: () => { throw new Error("quota"); } } });
    const state = setDevelopmentBuildNote(createDefaultDevelopmentBuildState(), "x".repeat(3_000));
    expect(state.note).toHaveLength(2_000);
    expect(saveDevelopmentBuildState("workspace-a", state)).toBe(false);
  });

  it("can mark a step complete and incomplete without duplicate ids", () => {
    const first = setDevelopmentBuildStepCompletion(createDefaultDevelopmentBuildState(), "scope", true);
    const duplicate = setDevelopmentBuildStepCompletion(first, "scope", true);
    const cleared = setDevelopmentBuildStepCompletion(duplicate, "scope", false);
    expect(duplicate.completedStepIds).toEqual(["scope"]);
    expect(cleared.completedStepIds).toEqual([]);
  });
});
