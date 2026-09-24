import { describe, expect, it } from "vitest";
import { localeDirection, locales, translate } from "./i18n";

const chatUxKeys = [
  "chat.clearAttachments",
  "chat.cancelled",
  "chat.stopGeneration",
  "chat.deleteConfirmTitle",
  "chat.copyResponse",
  "chat.regenerate",
  "chat.projectSelector",
  "chat.personalMemory",
  "chat.conversationHistory",
  "chat.projectContext",
  "chat.handoffHint",
  "chat.createDocument",
  "chat.createPresentation",
  "chat.createResearch",
  "chat.createImage",
  "chat.createSocial",
] as const;

describe("Chat UX 2.0 localization", () => {
  it("ships each Chat UX label in English, Arabic, and Kurdish", () => {
    for (const locale of locales) {
      for (const key of chatUxKeys) {
        expect(translate(locale, key)).not.toBe(key);
      }
    }
  });

  it("keeps Arabic and Kurdish Chat layouts in right-to-left direction", () => {
    expect(localeDirection("en")).toBe("ltr");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
  });
});
