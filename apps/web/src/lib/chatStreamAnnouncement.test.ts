import { describe, expect, it } from "vitest";
import { chatStreamAnnouncementKey } from "./chatStreamAnnouncement";

describe("chat stream announcements", () => {
  it("does not announce an idle transcript", () => {
    expect(chatStreamAnnouncementKey("idle")).toBeNull();
  });

  it("maps each lifecycle outcome to its localized key", () => {
    expect(chatStreamAnnouncementKey("generating")).toBe("chat.streamGenerating");
    expect(chatStreamAnnouncementKey("completed")).toBe("chat.streamCompleted");
    expect(chatStreamAnnouncementKey("failed")).toBe("chat.streamFailed");
    expect(chatStreamAnnouncementKey("cancelled")).toBe("chat.streamCancelled");
  });
});
