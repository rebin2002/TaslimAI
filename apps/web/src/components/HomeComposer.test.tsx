import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "./LocaleProvider";
import { HomeComposer } from "./HomeComposer";
import type { Project } from "../lib/api";

const project: Project = {
  id: "project-1",
  workspaceId: "workspace-1",
  name: "Launch plan",
  description: null,
  instructions: null,
  contextNotes: null,
  type: "General",
  status: "Active",
  createdAt: "2026-09-20T00:00:00Z",
  updatedAt: "2026-09-20T00:00:00Z",
  archivedAt: null,
};

describe("HomeComposer", () => {
  it("renders one multiline creation input with safe project and attachment controls", () => {
    const html = renderToStaticMarkup(<LocaleProvider><HomeComposer value="Plan a launch" onChange={vi.fn()} onSubmit={vi.fn()} busy={false} projects={[project]} projectId="" onProjectChange={vi.fn()} files={[]} onFilesChange={vi.fn()} onRemoveFile={vi.fn()} /></LocaleProvider>);
    expect(html).toContain("What would you like Taslim to create?");
    expect(html).toContain("Describe a document, image, presentation, movie, research task, or anything else...");
    expect(html).toContain("textarea");
    expect(html).toContain("Launch plan");
    expect(html).toContain("+ Attach");
    expect(html).toContain("Create");
    expect(html).not.toContain("Open Taslim Chat");
  });

  it("renders selected files without exposing internal storage metadata", () => {
    const file = new File([new Uint8Array(5000)], "brief.txt", { type: "text/plain" });
    const html = renderToStaticMarkup(<LocaleProvider><HomeComposer value="Summarize this" onChange={vi.fn()} onSubmit={vi.fn()} busy={false} projects={[]} projectId="" onProjectChange={vi.fn()} files={[file]} onFilesChange={vi.fn()} onRemoveFile={vi.fn()} /></LocaleProvider>);
    expect(html).toContain("brief.txt");
    expect(html).toContain("5 KB");
    expect(html).not.toContain("storageProvider");
    expect(html).not.toContain("storageKey");
  });
});
