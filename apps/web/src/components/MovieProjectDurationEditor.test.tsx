import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const editor = readFileSync(new URL("./MovieProjectDurationEditor.tsx", import.meta.url), "utf8");
const workspace = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");

describe("Full Movie draft duration editor", () => {
  it("uses the existing authorized antiforgery-protected update endpoint", () => {
    expect(api).toContain("updateMovieProjectDuration: (id: string, durationSeconds: number)");
    expect(api).toContain("/duration`, { method: \"PATCH\", body: JSON.stringify({ durationSeconds }) }, true)");
    expect(editor).toContain("api.updateMovieProjectDuration(projectId, number)");
    expect(editor).not.toContain("generateMovie");
    expect(editor).not.toContain("UsageTransaction");
  });

  it("never exposes an editor after scene planning starts and validates 120 minutes", () => {
    expect(workspace).toContain('canEditDraft={project.status === "Draft" && overview.scenes.total === 0 && overview.shots.total === 0}');
    expect(editor).toContain("if (busy || !canEditDraft || unchanged) return;");
    expect(editor).toContain("number >= 1 && number <= 7_200");
    expect(editor).toContain("Number.isSafeInteger(number)");
    expect(editor).toContain("type=\"number\"");
    expect(editor).toContain("max={7200}");
    expect(editor).toContain("Editing an established timeline requires a separately reviewed replan.");
  });

  it("resets stale form content and notices when project or server duration changes", () => {
    expect(editor).toContain("useEffect, useState");
    expect(editor).toContain("setValue(String(durationSeconds));");
    expect(editor).toContain("setNotice(\"\");");
    expect(editor).toContain("setError(\"\");");
    expect(editor).toContain("}, [projectId, durationSeconds]);");
  });

  it("checks the returned persisted project, refreshes the read model and localizes copy", () => {
    expect(editor).toContain("updated.id !== projectId || updated.durationSeconds !== number");
    expect(editor).toContain("await onUpdated()");
    expect(workspace).toContain("onRefresh={() => api.getMovieOverview(projectId).then(setOverview)}");
    expect(editor).toContain("en:");
    expect(editor).toContain("ar:");
    expect(editor).toContain("ku:");
  });
});
