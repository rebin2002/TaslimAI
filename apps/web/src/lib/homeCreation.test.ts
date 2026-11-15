import { describe, expect, it } from "vitest";
import { attachmentOnlyCreationPrompt, attachmentOnlyCreationPrompts, canSubmitHomeCreation, createHomeCreationSubmissionGate, isHomeCreationSubmitKey, resolveHomeCreationPrompt } from "./homeCreation";

describe("home creation prompt", () => {
  it("does not submit while Enter confirms an IME candidate or while Shift is held", () => {
    const key = { key: "Enter", shiftKey: false, isComposing: false, keyCode: 13 };
    expect(isHomeCreationSubmitKey(key)).toBe(true);
    expect(isHomeCreationSubmitKey({ ...key, isComposing: true })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...key, keyCode: 229 })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...key, shiftKey: true })).toBe(false);
    expect(isHomeCreationSubmitKey({ ...key, key: "Escape" })).toBe(false);
  });

  it("requires either instructions or an attachment", () => {
    expect(resolveHomeCreationPrompt("   ", 0)).toBeNull();
    expect(canSubmitHomeCreation("   ", 0)).toBe(false);
  });

  it("provides a deterministic review instruction for attachment-only creation", () => {
    expect(resolveHomeCreationPrompt("   ", 1)).toBe(attachmentOnlyCreationPrompt);
    expect(canSubmitHomeCreation("   ", 1)).toBe(true);
  });

  it("uses the active English, Arabic or Sorani locale for attachment-only Home creation", () => {
    for (const locale of ["en", "ar", "ku"] as const) {
      expect(resolveHomeCreationPrompt("  ", 1, locale)).toBe(attachmentOnlyCreationPrompts[locale]);
      expect(resolveHomeCreationPrompt("", 2, locale)).toBe(attachmentOnlyCreationPrompts[locale]);
      expect(resolveHomeCreationPrompt("  Summarize the report ", 1, locale)).toBe("Summarize the report");
      expect(resolveHomeCreationPrompt("  ", 0, locale)).toBeNull();
    }
    expect(attachmentOnlyCreationPrompts.ar).toContain("الملفات");
    expect(attachmentOnlyCreationPrompts.ku).toContain("فایل");
  });

  it("rejects repeated submission until the in-flight creation finishes", () => {
    const gate = createHomeCreationSubmissionGate();
    expect(gate.tryStart()).toBe(true);
    expect(gate.isActive()).toBe(true);
    expect(gate.tryStart()).toBe(false);
    expect(gate.tryStart()).toBe(false);
    gate.finish();
    expect(gate.isActive()).toBe(false);
    expect(gate.tryStart()).toBe(true);
    gate.finish();
  });

  it("revokes a pending submission when the authenticated Home scope unmounts", () => {
    const oldScope = createHomeCreationSubmissionGate();
    expect(oldScope.tryStart()).toBe(true);
    oldScope.cancel();
    expect(oldScope.isActive()).toBe(false);
    expect(oldScope.tryStart()).toBe(false);
    oldScope.finish();
    expect(oldScope.tryStart()).toBe(false);
    const nextScope = createHomeCreationSubmissionGate();
    expect(nextScope.tryStart()).toBe(true);
    expect(nextScope.isActive()).toBe(true);
    nextScope.finish();
  });

  it("preserves explicit instructions when attachments are also selected", () => {
    expect(resolveHomeCreationPrompt("  Summarize the PDF  ", 1)).toBe("Summarize the PDF");
  });
});
