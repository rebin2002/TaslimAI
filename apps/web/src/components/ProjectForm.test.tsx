import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Project } from "@/lib/api";
import { ProjectForm } from "./ProjectForm";

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string) => key,
  }),
}));

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

describe("ProjectForm accessibility contract", () => {
  it("labels the modal and provides an explicit initial focus target", () => {
    const html = renderToStaticMarkup(<ProjectForm project={project} onClose={vi.fn()} onSubmit={vi.fn()} />);

    expect(html).toContain('role="dialog"');
    expect(html).toContain('aria-modal="true"');
    expect(html).toContain('aria-labelledby="project-form-title"');
    expect(html).toContain('id="project-form-title"');
    expect(html).toContain("data-dialog-autofocus");
  });
});
