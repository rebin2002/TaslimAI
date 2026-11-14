import { describe, expect, it } from "vitest";
import type { StoredFile } from "./api";
import { isSocialSourceReady } from "./socialStudioState";

const source = (extractedTextLength: number | null): StoredFile => ({
  id: "file-1",
  workspaceId: "workspace-1",
  projectId: null,
  conversationId: null,
  originalFileName: "brief.txt",
  contentType: "text/plain",
  extension: ".txt",
  sizeBytes: 100,
  storageProvider: "test",
  status: "Ready",
  textExtractionStatus: "Ready",
  extractedTextLength,
  createdAt: "2026-01-01T00:00:00Z",
  processedAt: "2026-01-01T00:00:00Z",
});

describe("isSocialSourceReady", () => {
  it("rejects API-known empty extraction while accepting non-empty extraction", () => {
    expect(isSocialSourceReady(source(0))).toBe(false);
    expect(isSocialSourceReady(source(null))).toBe(false);
    expect(isSocialSourceReady(source(12))).toBe(true);
  });
});
