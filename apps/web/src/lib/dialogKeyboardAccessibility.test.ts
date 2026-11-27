import {
  closeDialog,
  dialogStackSize,
  isTabbableElement,
  isTopDialog,
  openDialog,
  raiseDialog,
  shouldHandleDialogKey,
} from "./useDialogAccessibility";
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

describe("useDialogAccessibility focusability filter (independent review findings)", () => {
  // The shared hook must only enumerate nodes the browser itself can move
  // sequential focus into. These cases reproduce every gap the review found:
  // CSS visibility through ancestors, hidden ancestors, disabled fieldsets
  // and inert subtrees.
  class FakeElement {
    tagName: string;
    attrs = new Map<string, string>();
    children: FakeElement[] = [];
    parent: FakeElement | null = null;
    hidden = false;
    disabled = false;
    type = "";
    checkVisibilityValue: boolean | null | "absent" | "throws" = null;
    constructor(tagName: string, attrs: string[] = []) {
      this.tagName = tagName;
      for (const attr of attrs) this.attrs.set(attr, "");
    }
    set(attr: string, value = ""): this {
      this.attrs.set(attr, value);
      if (attr === "hidden") this.hidden = true;
      if (attr === "disabled") this.disabled = true;
      return this;
    }
    append(child: FakeElement): this {
      child.parent = this;
      this.children.push(child);
      return this;
    }
    getAttribute(name: string): string | null {
      return this.attrs.get(name) ?? null;
    }
    hasAttribute(name: string): boolean {
      return this.attrs.has(name);
    }
    contains(element: FakeElement): boolean {
      return this === element || this.children.some((child) => child.contains(element));
    }
    matches(matcher: string): boolean {
      if (matcher === "[hidden]" && this.attrs.has("hidden")) return true;
      if (matcher === '[aria-hidden="true"]' && this.attrs.get("aria-hidden") === "true") return true;
      if (matcher === "[inert]" && this.attrs.has("inert")) return true;
      if (matcher === "fieldset[disabled]" && this.tagName === "FIELDSET" && this.attrs.has("disabled")) return true;
      if (matcher === "legend" && this.tagName === "LEGEND") return true;
      return false;
    }
    closest(matcher: string): FakeElement | null {
      if (this.matches(matcher)) return this;
      return this.parent ? this.parent.closest(matcher) : null;
    }
    get parentElement(): FakeElement | null {
      return this.parent;
    }
  }
  type StyleMap = Partial<Record<string, { display?: string; visibility?: string }>>;
  function evaluate(element: FakeElement, styles: StyleMap = {}) {
    const probe = element as unknown as HTMLElement;
    if (element.checkVisibilityValue === "absent") {
      delete (probe as { checkVisibility?: unknown }).checkVisibility;
    } else if (element.checkVisibilityValue === "throws") {
      (probe as { checkVisibility: unknown }).checkVisibility = () => {
        throw new Error("no options support");
      };
    } else if (element.checkVisibilityValue !== null) {
      (probe as { checkVisibility: unknown }).checkVisibility = () => element.checkVisibilityValue;
    }
    const readStyle = (node: Element) => {
      const target = node as unknown as FakeElement;
      const key = `${target.tagName}#${target.getAttribute("id") ?? ""}`;
      const entry = styles[key] ?? {};
      return { display: entry.display ?? "block", visibility: entry.visibility ?? "visible" };
    };
    return isTabbableElement(probe, readStyle);
  }
  function probe(id: string, tagName = "BUTTON", attrs: string[] = []): FakeElement {
    const element = new FakeElement(tagName, attrs);
    element.set("id", id);
    return element;
  }

  it("keeps ordinary enabled, rendered controls tabbable", () => {
    const button = probe("plain");
    button.checkVisibilityValue = true;
    expect(evaluate(button)).toBe(true);
    expect(evaluate(probe("link", "A", ["href"]), { "A#link": { display: "block" } })).toBe(true);
  });

  it("excludes the hidden attribute on the element and on ancestors", () => {
    const hiddenSelf = probe("hidden-self");
    hiddenSelf.hidden = true;
    hiddenSelf.checkVisibilityValue = true;
    expect(evaluate(hiddenSelf)).toBe(false);
    const parent = new FakeElement("DIV", ["hidden"]);
    const child = probe("inside-hidden-ancestor");
    child.checkVisibilityValue = true;
    parent.append(child);
    expect(evaluate(child)).toBe(false);
  });

  it("excludes aria-hidden on the element and on ancestors", () => {
    const self = probe("aria-self");
    self.set("aria-hidden", "true");
    self.checkVisibilityValue = true;
    expect(evaluate(self)).toBe(false);
    const wrapper = new FakeElement("DIV").set("aria-hidden", "true");
    const inside = probe("aria-ancestor");
    inside.checkVisibilityValue = true;
    wrapper.append(inside);
    expect(evaluate(inside)).toBe(false);
  });

  it("excludes inert subtrees", () => {
    const background = new FakeElement("SECTION", ["inert"]);
    const inside = probe("inside-inert");
    inside.checkVisibilityValue = true;
    background.append(inside);
    expect(evaluate(inside)).toBe(false);
  });

  it("excludes disabled controls, disabled fieldset members and hidden inputs", () => {
    const disabledButton = probe("disabled-btn");
    disabledButton.set("disabled");
    disabledButton.checkVisibilityValue = true;
    expect(evaluate(disabledButton)).toBe(false);
    const fieldset = new FakeElement("FIELDSET").set("disabled");
    const textInput = probe("fieldset-input", "INPUT");
    textInput.checkVisibilityValue = true;
    fieldset.append(textInput);
    expect(evaluate(textInput)).toBe(false);
    const legend = new FakeElement("LEGEND");
    fieldset.append(legend);
    const legendButton = probe("legend-button");
    legendButton.checkVisibilityValue = true;
    legend.append(legendButton);
    expect(evaluate(legendButton)).toBe(true);
    const hiddenInput = probe("secret", "INPUT");
    hiddenInput.type = "hidden";
    hiddenInput.checkVisibilityValue = true;
    expect(evaluate(hiddenInput)).toBe(false);
  });

  it("exempts only controls inside each disabled fieldset's first direct legend", () => {
    const outer = new FakeElement("FIELDSET").set("disabled");
    const firstLegend = new FakeElement("LEGEND");
    const firstLegendButton = probe("first-legend-button");
    firstLegendButton.checkVisibilityValue = true;
    firstLegend.append(firstLegendButton);
    outer.append(firstLegend);

    const laterLegend = new FakeElement("LEGEND");
    const laterLegendButton = probe("later-legend-button");
    laterLegendButton.checkVisibilityValue = true;
    laterLegend.append(laterLegendButton);
    outer.append(laterLegend);
    const outsideLegendButton = probe("outside-legend-button");
    outsideLegendButton.checkVisibilityValue = true;
    outer.append(outsideLegendButton);

    expect(evaluate(firstLegendButton)).toBe(true);
    expect(evaluate(laterLegendButton)).toBe(false);
    expect(evaluate(outsideLegendButton)).toBe(false);
  });

  it("applies nested disabled fieldsets independently, including their own first-legend exception", () => {
    const outer = new FakeElement("FIELDSET").set("disabled");
    const outerLegend = new FakeElement("LEGEND");
    outer.append(outerLegend);
    const nestedInsideOuterLegend = new FakeElement("FIELDSET").set("disabled");
    outerLegend.append(nestedInsideOuterLegend);
    const nestedFirstLegend = new FakeElement("LEGEND");
    nestedInsideOuterLegend.append(nestedFirstLegend);
    const doublyExempt = probe("nested-first-legend");
    doublyExempt.checkVisibilityValue = true;
    nestedFirstLegend.append(doublyExempt);
    const nestedOutsideFirstLegend = probe("nested-after-legend");
    nestedOutsideFirstLegend.checkVisibilityValue = true;
    nestedInsideOuterLegend.append(nestedOutsideFirstLegend);

    const nestedOutsideOuterLegend = new FakeElement("FIELDSET").set("disabled");
    outer.append(nestedOutsideOuterLegend);
    const innerLegend = new FakeElement("LEGEND");
    nestedOutsideOuterLegend.append(innerLegend);
    const blockedByOuter = probe("nested-inner-legend");
    blockedByOuter.checkVisibilityValue = true;
    innerLegend.append(blockedByOuter);

    expect(evaluate(doublyExempt)).toBe(true);
    expect(evaluate(nestedOutsideFirstLegend)).toBe(false);
    expect(evaluate(blockedByOuter)).toBe(false);
  });

  it("excludes negative tabindex values", () => {
    const minusOne = probe("minus-one");
    minusOne.set("tabindex", "-1");
    minusOne.checkVisibilityValue = true;
    expect(evaluate(minusOne)).toBe(false);
    const minusTwo = probe("minus-two", "A", ["href"]).set("tabindex", "-2");
    minusTwo.checkVisibilityValue = true;
    expect(evaluate(minusTwo)).toBe(false);
    const zero = probe("zero");
    zero.set("tabindex", "0");
    zero.checkVisibilityValue = true;
    expect(evaluate(zero)).toBe(true);
  });

  it("excludes CSS-invisible controls via checkVisibility and via the ancestor-walk fallback", () => {
    const viaApi = probe("vis-hidden-check");
    viaApi.set("id", "vis-hidden-check");
    viaApi.checkVisibilityValue = false;
    expect(evaluate(viaApi, {})).toBe(false);
    const wrapper = new FakeElement("DIV");
    wrapper.set("id", "gone-wrapper");
    const child = probe("under-display-none", "INPUT");
    child.set("id", "under-display-none");
    child.checkVisibilityValue = "absent";
    wrapper.append(child);
    expect(evaluate(child, { "DIV#gone-wrapper": { display: "none" } })).toBe(false);
    const visWrapper = new FakeElement("DIV");
    visWrapper.set("id", "vis-wrapper");
    const visChild = probe("under-visibility", "INPUT");
    visChild.set("id", "under-visibility");
    visChild.checkVisibilityValue = "absent";
    visWrapper.append(visChild);
    expect(evaluate(visChild, { "DIV#vis-wrapper": { visibility: "hidden" } })).toBe(false);
    const throwing = probe("old-engine");
    throwing.checkVisibilityValue = "throws";
    expect(evaluate(throwing)).toBe(true);
    const fullyVisible = probe("normal-input", "INPUT");
    fullyVisible.checkVisibilityValue = "absent";
    expect(evaluate(fullyVisible)).toBe(true);
  });
});

describe("useDialogAccessibility multi-instance stack gating", () => {
  // Two dialog hooks mounted at once (AssetsView carries a filter sheet and
  // an edit modal) previously processed the same Escape and pulled focus in
  // opposite directions. Exactly one instance — the top of the stack — may
  // answer a document-level key now, which makes both failure modes
  // structurally impossible.
  const makeHandle = () => ({ token: Symbol("dialog") });
  it("keeps the nested dialog on top when both layout effects mount together", () => {
    const innerElement = { contains: () => false } as unknown as HTMLElement;
    const outerElement = { contains: (element: HTMLElement) => element === innerElement } as unknown as HTMLElement;
    const outer = { token: Symbol("outer"), element: outerElement };
    const inner = { token: Symbol("inner"), element: innerElement };
    openDialog(inner);
    openDialog(outer);
    try {
      expect(shouldHandleDialogKey(inner, "Escape")).toBe(true);
      expect(shouldHandleDialogKey(outer, "Tab")).toBe(false);
    } finally {
      closeDialog(inner);
      closeDialog(outer);
    }
  });

  it("admits each open instance once and keeps a single top of stack", () => {
    const first = makeHandle();
    const second = makeHandle();
    openDialog(first);
    openDialog(first);
    openDialog(second);
    expect(dialogStackSize()).toBe(2);
    expect(isTopDialog(second)).toBe(true);
    expect(isTopDialog(first)).toBe(false);
    raiseDialog(first);
    expect(isTopDialog(first)).toBe(true);
    expect(isTopDialog(second)).toBe(false);
    raiseDialog(makeHandle());
    expect(dialogStackSize()).toBe(2);
    closeDialog(first);
    expect(isTopDialog(second)).toBe(true);
    closeDialog(second);
    expect(dialogStackSize()).toBe(0);
  });
  it("routes Escape and Tab to exactly one instance at a time", () => {
    const outer = makeHandle();
    const inner = makeHandle();
    openDialog(outer);
    openDialog(inner);
    const handlers = [outer, inner].filter((handle) => shouldHandleDialogKey(handle, "Escape"));
    expect(handlers).toHaveLength(1);
    expect(shouldHandleDialogKey(inner, "Escape")).toBe(true);
    expect(shouldHandleDialogKey(outer, "Escape")).toBe(false);
    expect(shouldHandleDialogKey(outer, "Tab")).toBe(false);
    expect(shouldHandleDialogKey(inner, "Enter")).toBe(false);
    raiseDialog(outer);
    expect(shouldHandleDialogKey(outer, "Escape")).toBe(true);
    expect(shouldHandleDialogKey(inner, "Escape")).toBe(false);
    closeDialog(outer);
    closeDialog(inner);
    expect(dialogStackSize()).toBe(0);
    expect(shouldHandleDialogKey(inner, "Escape")).toBe(false);
  });
});
