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
    expect(panelSource).toContain("isConsistentMovieClipSegmentationResponse(result, projectId, totalDurationSeconds)");
  });

  it("retries transient preview failures through the same read-only API", () => {
    expect(panelSource).toContain("api.getMovieClipSegmentationPreview(projectId)");
    expect(panelSource).toContain("function retryPreview()");
    expect(panelSource).toContain("setRetryKey((previous) => previous + 1)");
    expect(panelSource).toContain("[projectId, totalDurationSeconds, retryKey]");
    expect(panelSource).toContain('type="button" className="movie-workspace-button" onClick={retryPreview}');
    expect(panelSource).toContain("setPreview(null)");
    expect(panelSource).toContain("setError(false)");
    expect(panelSource).toContain("if (active)");
    expect(panelSource).not.toContain("api.generateMovie");
    expect(panelSource).not.toContain('method: "POST"');
    expect(panelSource).not.toContain('method: "PATCH"');
    expect(panelSource).toContain('retry: "Retry preview"');
    expect(panelSource).toContain('retry: "إعادة محاولة المعاينة"');
    expect(panelSource).toContain('retry: "دووبارە هەوڵ بدە"');
  });

  it("shows only validated scene-aligned slot counts, not rendered scenes or billable jobs", () => {
    expect(apiSource).toContain("canSplitByScene?: boolean");
    expect(apiSource).toContain("sceneSegments?: Array<");
    expect(panelSource).toContain("isConsistentMovieClipSegmentationResponse(result, projectId, totalDurationSeconds)");
    expect(panelSource).toContain("preview.canSplitByScene && preview.sceneSegments?.length");
    expect(panelSource).toContain("labels.sceneSlots");
    expect(panelSource).toContain("labels.scenePending");
    expect(panelSource).not.toContain("api.generateMovie");
    expect(panelSource).not.toContain("method: \"POST\"");
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
