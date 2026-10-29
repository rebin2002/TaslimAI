import { describe, expect, it } from "vitest";
import { attachmentOnlyCreationPrompt, canSubmitHomeCreation, resolveHomeCreationPrompt } from "./homeCreation";

describe("home creation prompt", () => {
  it("requires either instructions or an attachment", () => {
    expect(resolveHomeCreationPrompt("   ", 0)).toBeNull();
    expect(canSubmitHomeCreation("   ", 0)).toBe(false);
  });

  it("provides a deterministic review instruction for attachment-only creation", () => {
    expect(resolveHomeCreationPrompt("   ", 1)).toBe(attachmentOnlyCreationPrompt);
    expect(canSubmitHomeCreation("   ", 1)).toBe(true);
  });

  it("preserves explicit instructions when attachments are also selected", () => {
    expect(resolveHomeCreationPrompt("  Summarize the PDF  ", 1)).toBe("Summarize the PDF");
  });
});
