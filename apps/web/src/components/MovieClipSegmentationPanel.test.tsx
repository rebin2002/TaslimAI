import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const panelSource = readFileSync(new URL("./MovieClipSegmentationPanel.tsx", import.meta.url), "utf8");
const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const apiSource = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");

describe("Full Movie duration-only segmentation preview", () => {
  it("uses only the authenticated read path, with no provider submission or usage mutation", () => {
    expect(apiSource).toContain("getMovieClipSegmentationPreview: (id: string) => request<MovieClipSegmentationPreview>");
    expect(apiSource).toContain("/clip-segmentation-preview`");
    expect(panelSource).toContain("api.getMovieClipSegmentationPreview(projectId)");
    expect(panelSource).not.toContain("api.generateMovie");
    expect(panelSource).not.toContain("api.queue");
    expect(panelSource).not.toContain("method: \"POST\"");
    expect(panelSource).not.toContain("method: \"PATCH\"");
  });

  it("never labels a duration split as render authorization or a produced film", () => {
    expect(panelSource).toContain("Duration-only calculation.");
    expect(panelSource).toContain("does not plan shots, verify continuity, price or authorize generation");
    expect(panelSource).toContain("!preview.canSplitByDuration");
    expect(panelSource).toContain("preview.segments.length");
    expect(panelSource).toContain("result.movieProjectId !== projectId");
    expect(panelSource).toContain("result.totalDurationSeconds !== totalDurationSeconds");
  });

  it("shows the preview in the Full Movie overview and resets on duration change", () => {
    expect(workspaceSource).toContain("MovieClipSegmentationPanel");
    expect(workspaceSource).toContain("key={`${project.id}-${project.durationSeconds}`}");
    expect(workspaceSource).toContain("totalDurationSeconds={project.durationSeconds}");
    expect(panelSource).toContain("en:");
    expect(panelSource).toContain("ar:");
    expect(panelSource).toContain("ku:");
  });
});
