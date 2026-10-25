import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./DevelopmentLearningGuide.tsx", import.meta.url), "utf8");

describe("development learning guide state safety", () => {
  it("queues rapid progress updates from the latest state before persisting", () => {
    expect(source).toContain("const next = update(guideStateRef.current);");
    expect(source).toContain("guideStateRef.current = next;");
    expect(source).toContain("setDevelopmentLessonCompletion(current");
    expect(source).toContain("setDevelopmentLearningNotes(current");
    expect(source).toContain("loadedWorkspaceId !== workspace.id");
    expect(source).toContain("saveDevelopmentLearningState(workspace.id, next)");
    expect(source).not.toContain("setDevelopmentLessonCompletion(guideState");
  });
});
