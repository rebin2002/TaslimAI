import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createDefaultDevelopmentLearningState,
  developmentLearningStorageKey,
  isDevelopmentLessonComplete,
  readDevelopmentLearningState,
  saveDevelopmentLearningState,
  setDevelopmentLearningNotes,
  setDevelopmentLessonCompletion,
} from "./developmentLearningState";

afterEach(() => vi.unstubAllGlobals());

describe("development learning state", () => {
  it("uses a distinct durable key for each workspace", () => {
    expect(developmentLearningStorageKey("workspace-a")).not.toBe(developmentLearningStorageKey("workspace-b"));
    expect(developmentLearningStorageKey("workspace-a")).toBe("taslim:development-learning:workspace-a");
  });

  it("persists progress only inside the active workspace namespace", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { localStorage: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
    } });
    const completed = setDevelopmentLessonCompletion(createDefaultDevelopmentLearningState(), "frontend-a11y", true);
    expect(saveDevelopmentLearningState("workspace-a", completed)).toBe(true);
    expect(isDevelopmentLessonComplete(readDevelopmentLearningState("workspace-a"), "frontend-a11y")).toBe(true);
    expect(isDevelopmentLessonComplete(readDevelopmentLearningState("workspace-b"), "frontend-a11y")).toBe(false);
  });

  it("recovers safely from malformed or untrusted stored data", () => {
    vi.stubGlobal("window", { localStorage: { getItem: () => "{not-json" } });
    expect(readDevelopmentLearningState("workspace-a")).toEqual(createDefaultDevelopmentLearningState());

    vi.stubGlobal("window", { localStorage: { getItem: () => JSON.stringify({ version: 1, activeTrack: "provider-secret", completedLessonIds: ["ok", 7, " ok ", "ok"], notes: "x".repeat(3_000), updatedAt: "safe" }) } });
    const recovered = readDevelopmentLearningState("workspace-a");
    expect(recovered.activeTrack).toBe("foundations");
    expect(recovered.completedLessonIds).toEqual(["ok"]);
    expect(recovered.notes).toHaveLength(2_000);
    expect(recovered.updatedAt).toBe("safe");
  });

  it("bounds notes and tolerates unavailable storage without throwing", () => {
    vi.stubGlobal("window", { localStorage: { setItem: () => { throw new Error("quota"); } } });
    const state = setDevelopmentLearningNotes(createDefaultDevelopmentLearningState(), "x".repeat(3_000));
    expect(state.notes).toHaveLength(2_000);
    expect(saveDevelopmentLearningState("workspace-a", state)).toBe(false);
  });

  it("can mark a lesson complete and incomplete without duplicate ids", () => {
    const first = setDevelopmentLessonCompletion(createDefaultDevelopmentLearningState(), "foundations-git", true);
    const duplicate = setDevelopmentLessonCompletion(first, "foundations-git", true);
    const cleared = setDevelopmentLessonCompletion(duplicate, "foundations-git", false);
    expect(duplicate.completedLessonIds).toEqual(["foundations-git"]);
    expect(cleared.completedLessonIds).toEqual([]);
  });
});
