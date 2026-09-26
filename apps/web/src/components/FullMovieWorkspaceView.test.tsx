import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(
  new URL("./FullMovieWorkspaceView.tsx", import.meta.url),
  "utf8",
);
const shotDesignerSource = readFileSync(
  new URL("./ShotDesigner.tsx", import.meta.url),
  "utf8",
);
const createSource = readFileSync(
  new URL("./MovieStudioView.tsx", import.meta.url),
  "utf8",
);

describe("Full Movie workspace foundation", () => {
  it("keeps the requested restrained production map in order", () => {
    const labels = [
      "Overview",
      "Story",
      "Cast",
      "World",
      "Scenes",
      "Storyboard",
      "Production",
      "Edit",
      "Audio",
      "QC",
      "Exports",
      "Team",
    ];
    const positions = labels.map((label) =>
      workspaceSource.indexOf(`label: "${label}"`),
    );

    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
  });

  it("uses durable project routes and marks future surfaces honestly", () => {
    expect(workspaceSource).toContain(
      "/create/movie/${project.id}/${item.slug}",
    );
    expect(workspaceSource).toContain("Foundation surface");
    expect(workspaceSource).toContain("No generated footage yet");
    expect(workspaceSource).toContain("Team controls are not connected yet");
  });

  it("keeps Quick Movie on a separate compact result path", () => {
    expect(createSource).toContain('mode === "Full"');
    expect(createSource).toContain(
      "router.push(`/create/movie/${result.project.id}/overview`)",
    );
    expect(createSource).toContain("Quick Movie stays intentionally small");
    expect(createSource).toContain("function QuickMovieResult");
  });

  it("mounts Shot Designer from the scene inspector without mutating the guide", () => {
    expect(workspaceSource).toContain(
      "<ShotDesigner scene={scene} guide={guide} presets={presets}",
    );
    expect(workspaceSource).toContain("api.addMovieShot(sceneId");
    expect(shotDesignerSource).toContain(
      "A shot override never rewrites the Movie Guide",
    );
    expect(shotDesignerSource).toContain(
      "Guide stays locked; this saves as a shot-level override",
    );
    expect(shotDesignerSource).toContain("Native");
    expect(shotDesignerSource).toContain("Translated");
    expect(shotDesignerSource).toContain("Simulated/Post");
    expect(shotDesignerSource).toContain("Unsupported");
  });
});
