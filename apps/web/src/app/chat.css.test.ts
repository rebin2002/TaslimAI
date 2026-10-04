import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const chatStyles = readFileSync(new URL("./chat.css", import.meta.url), "utf8");

describe("Chat presentation styles", () => {
  it("defines the metadata and handoff layout primitives", () => {
    for (const selector of [
      ".chat-context-bar",
      ".chat-context-item",
      ".chat-context-item strong",
      ".chat-project-selector",
      ".chat-creator-handoff",
      ".chat-creator-handoff > div",
      ".chat-creator-handoff button",
    ]) {
      expect(chatStyles).toContain(selector);
    }

    expect(chatStyles).toContain("grid-template-columns: repeat(3, minmax(0, 1fr));");
    expect(chatStyles).toContain("gap: 9px;");
    expect(chatStyles).toContain("gap: 6px;");
  });

  it("keeps the empty state and composer compact across desktop and mobile", () => {
    expect(chatStyles).toContain(".chat-empty {\n  min-height: 250px;\n}");
    expect(chatStyles).toContain(".chat-composer textarea {\n    min-height: 40px;\n  }");
    expect(chatStyles).toContain("@media (max-width: 760px)");
    expect(chatStyles).toContain("grid-template-columns: 1fr;");
  });

  it("includes RTL and reduced-motion safeguards for the new controls", () => {
    expect(chatStyles).toContain('[dir="rtl"] .chat-context-bar');
    expect(chatStyles).toContain('[dir="rtl"] .chat-creator-handoff > div');
    expect(chatStyles).toContain("@media (prefers-reduced-motion: reduce)");
  });
});
