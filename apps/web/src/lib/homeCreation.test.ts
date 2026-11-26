import { describe, expect, it } from "vitest";
import {
  attachmentOnlyCreationPrompt,
  attachmentOnlyCreationPrompts,
  canSubmitHomeCreation,
  createHomeCreationSubmissionGate,
  isHomeCreationSubmitKey,
  resolveHomeCreationPrompt,
} from "./homeCreation";

describe("home creation prompt", () => {
  it("requires either instructions or an attachment", () => {
    expect(resolveHomeCreationPrompt("   ", 0)).toBeNull();
    expect(canSubmitHomeCreation("   ", 0)).toBe(false);
  });

  it("provides a localized deterministic prompt for attachment-only creation", () => {
    for (const locale of ["en", "ar", "ku"] as const) {
      expect(resolveHomeCreationPrompt("   ", 1, locale)).toBe(attachmentOnlyCreationPrompts[locale]);
      expect(resolveHomeCreationPrompt("", 2, locale)).toBe(attachmentOnlyCreationPrompts[locale]);
      expect(resolveHomeCreationPrompt("  ", 0, locale)).toBeNull();
    }
    expect(attachmentOnlyCreationPrompts.en).toBe(attachmentOnlyCreationPrompt);
  });

  it("preserves explicit instructions when a file is attached", () => {
    expect(resolveHomeCreationPrompt("  Summarize the PDF  ", 1, "en")).toBe("Summarize the PDF");
  });

  it("admits only a completed plain Enter, not Shift+Enter or IME confirmation", () => {
    const plainEnter = { key: "Enter", shiftKey: false, isComposing: false, keyCode: 13 };
    expect(isHomeCreationSubmitKey(plainEnter)).toBe(true);
    expect(isHomeCreationSubmitKey({ ...plainEnter, shiftKey: true })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...plainEnter, isComposing: true })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...plainEnter, keyCode: 229 })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...plainEnter, key: "Escape" })).toBe(false);
  });

  it("prevents duplicate in-flight submissions and revokes an old identity scope", () => {
    const gate = createHomeCreationSubmissionGate();
    expect(gate.tryStart()).toBe(true);
    expect(gate.isActive()).toBe(true);
    expect(gate.tryStart()).toBe(false);
    gate.finish();
    expect(gate.isActive()).toBe(false);
    expect(gate.tryStart()).toBe(true);
    gate.cancel();
    expect(gate.isActive()).toBe(false);
    expect(gate.tryStart()).toBe(false);
  });
});
