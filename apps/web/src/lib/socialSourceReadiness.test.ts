import { describe, expect, it } from "vitest";
import type { StoredFile } from "./api";
import { isSocialSourceReady } from "./socialStudioState";

const source = (overrides: Partial<StoredFile> = {}): StoredFile => ({
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
  extractedTextLength: 12,
  createdAt: "2026-01-01T00:00:00Z",
  processedAt: "2026-01-01T00:00:00Z",
  ...overrides,
});

describe("isSocialSourceReady extraction boundary", () => {
  it("rejects API-known empty extraction lengths", () => {
    expect(isSocialSourceReady(source({ extractedTextLength: 0 }))).toBe(false);
    expect(isSocialSourceReady(source({ extractedTextLength: null }))).toBe(false);
    expect(isSocialSourceReady(source({ extractedTextLength: -1 }))).toBe(false);
  });

  it("accepts a source with a non-empty extraction", () => {
    expect(isSocialSourceReady(source({ extractedTextLength: 12 }))).toBe(true);
  });

  it("keeps compatibility with older callers that omit the optional runtime field", () => {
    const { extractedTextLength, ...legacySource } = source();
    expect(extractedTextLength).toBe(12);
    expect(isSocialSourceReady(legacySource as StoredFile)).toBe(true);
  });

  it("still enforces the base readiness contract", () => {
    expect(isSocialSourceReady(source({ status: "Processing" }))).toBe(false);
    expect(isSocialSourceReady(source({ textExtractionStatus: "Failed" }))).toBe(false);
    expect(isSocialSourceReady(source({ extension: ".png", originalFileName: "cover.png", contentType: "image/png" }))).toBe(false);
  });
});
