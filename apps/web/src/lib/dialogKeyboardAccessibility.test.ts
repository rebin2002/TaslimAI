import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// Dependency-free source contract: every dialog surface that this lane hardens
// must keep its explicit dialog semantics, its hook wiring and its initial
// focus target. These are the exact attributes the shared hook and assistive
// technology read at runtime, so dropping any one of them is a regression the
// hosted Browser E2E would otherwise only catch late.

function source(relativePath: string): string {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}

const hook = source("./useDialogAccessibility.ts");

describe("useDialogAccessibility hook contract", () => {
  it("exports the shared hook with focus containment, Escape and restoration", () => {
    expect(hook).toContain("export function useDialogAccessibility");
    expect(hook).toContain('"focusin"');
    expect(hook).toContain('"Escape"');
    expect(hook).toContain("data-dialog-autofocus");
    expect(hook).toContain("resolveFocusTarget");
  });
});

describe("ProjectForm dialog contract", () => {
  const file = source("../components/ProjectForm.tsx");
  it("wires the hook and exposes labelled modal semantics", () => {
    expect(file).toContain('import { useDialogAccessibility } from "@/lib/useDialogAccessibility";');
    expect(file).toContain("ref={dialogRef}");
    expect(file).toContain('role="dialog"');
    expect(file).toContain('aria-modal="true"');
    expect(file).toContain("aria-labelledby={dialogTitleId}");
    expect(file).toContain("id={dialogTitleId}");
    expect(file).toContain("tabIndex={-1}");
    expect(file).toContain("data-dialog-autofocus");
    expect(file).toContain("restoreFocusRef");
  });
});

describe("ProjectsView opener contract", () => {
  const file = source("../components/ProjectsView.tsx");
  it("records the opening control on every path that mounts the form", () => {
    expect(file).toContain("const formOpenerRef = useRef<HTMLElement | null>(null);");
    expect(file).toContain("function openForm(project?: Project)");
    expect(file).toContain("formOpenerRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;");
    expect(file).toContain("restoreFocusRef={formOpenerRef}");
    expect(file).toContain("onClick={() => openForm()}");
    expect(file).toContain("onClick={() => openForm(project)}");
  });
});

describe("MemoryView dialog contract", () => {
  const file = source("../components/MemoryView.tsx");
  it("exposes labelled modal semantics with an explicit initial focus target", () => {
    expect(file).toContain('import { useDialogAccessibility } from "@/lib/useDialogAccessibility";');
    expect(file).toContain("useDialogAccessibility<HTMLFormElement>(formOpen, closeForm)");
    expect(file).toContain('ref={dialogRef} className="memory-form modal-card" role="dialog" aria-modal="true" aria-labelledby="memory-form-title" tabIndex={-1}');
    expect(file).toContain('id="memory-form-title"');
    expect(file).toContain("data-dialog-autofocus");
  });
});

describe("ChatView delete confirmation dialog contract", () => {
  const file = source("../components/ChatView.tsx");
  it("keeps the destructive confirmation focus-contained", () => {
    expect(file).toContain('import { useDialogAccessibility } from "@/lib/useDialogAccessibility";');
    expect(file).toContain("useDialogAccessibility(deleteConfirmOpen, () => setDeleteConfirmOpen(false))");
    expect(file).toContain('ref={deleteDialogRef} className="modal-card chat-delete-dialog" role="dialog" aria-modal="true" aria-labelledby="chat-delete-title" tabIndex={-1}');
  });
});

describe("AssetsView dialog contracts", () => {
  const file = source("../components/AssetsView.tsx");
  it("hardens the filter sheet", () => {
    expect(file).toContain('import { useDialogAccessibility } from "@/lib/useDialogAccessibility";');
    expect(file).toContain("useDialogAccessibility(filtersOpen, () => setFiltersOpen(false), filterOpenerRef)");
    expect(file).toContain('ref={filterDialogRef} className="asset-filter-sheet" role="dialog" aria-modal="true" aria-labelledby="asset-filter-title" tabIndex={-1}');
    expect(file).toContain("onClick={openFilters}");
  });
  it("hardens the asset edit modal with a label and an initial focus target", () => {
    expect(file).toContain("useDialogAccessibility<HTMLFormElement>(Boolean(editing), () => setEditing(null), editOpenerRef)");
    expect(file).toContain('ref={editDialogRef} className="modal-card asset-edit-modal" role="dialog" aria-modal="true" aria-labelledby="asset-edit-title" tabIndex={-1}');
    expect(file).toContain('id="asset-edit-title"');
    expect(file).toContain("data-dialog-autofocus");
    expect(file).toContain("startEdit(asset, detailOpenerRef.current)");
  });
});
