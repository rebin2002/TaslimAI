import { describe, expect, it } from "vitest";
import { beginProjectFileUpload, isCurrentProjectFileUpload, type ProjectFileUploadScope } from "./ProjectFilesSection";

describe("ProjectFilesSection upload freshness", () => {
  it("rejects a completion after navigation changes the active project", () => {
    const ref = { current: 0 };
    const previous: ProjectFileUploadScope = { workspaceId: "workspace-a", projectId: "project-a" };
    const next: ProjectFileUploadScope = { workspaceId: "workspace-b", projectId: "project-b" };
    const upload = beginProjectFileUpload(ref, previous);

    expect(isCurrentProjectFileUpload(upload, ref, previous)).toBe(true);
    expect(isCurrentProjectFileUpload(upload, ref, next)).toBe(false);
  });

  it("rejects a completion after a newer upload starts in the same project", () => {
    const ref = { current: 0 };
    const scope: ProjectFileUploadScope = { workspaceId: "workspace-a", projectId: "project-a" };
    const stale = beginProjectFileUpload(ref, scope);
    const current = beginProjectFileUpload(ref, scope);

    expect(isCurrentProjectFileUpload(stale, ref, scope)).toBe(false);
    expect(isCurrentProjectFileUpload(current, ref, scope)).toBe(true);
  });
});
