import { describe, expect, it } from "vitest";
import {
  filterStudioDownloadableRepresentations,
  isStudioRepresentationDownloadable,
  readStudioRepresentationDownloadability,
} from "./studioRepresentationAvailability";

/**
 * #1049 consumer contract, verified with synthetic, provider-free fixtures.
 * Nothing here touches a network, a store, or a real asset.
 */

const DOCX_ID = "00000000-0000-4000-8000-000000000011";
const PDF_ID = "00000000-0000-4000-8000-000000000012";

function snapshot() {
  return [
    { id: DOCX_ID, type: "docx", fileName: "synthetic-brief.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
    { id: PDF_ID, type: "pdf", fileName: "synthetic-brief.pdf", contentType: "application/pdf" },
  ];
}

describe("readStudioRepresentationDownloadability", () => {
  it("returns null for every payload shape that predates the #1117 contract", () => {
    expect(readStudioRepresentationDownloadability(undefined)).toBeNull();
    expect(readStudioRepresentationDownloadability(null)).toBeNull();
    expect(readStudioRepresentationDownloadability({})).toBeNull();
    expect(readStudioRepresentationDownloadability({ id: "job-1" })).toBeNull();
    expect(readStudioRepresentationDownloadability({ representationDownloads: null })).toBeNull();
    // A non-array companion is not facts; fail open to the legacy behaviour.
    expect(readStudioRepresentationDownloadability({ representationDownloads: "https://cdn.invalid/leak-check" })).toBeNull();
  });

  it("returns an empty map for an explicitly delivered empty list", () => {
    const facts = readStudioRepresentationDownloadability({ representationDownloads: [] });
    expect(facts).not.toBeNull();
    expect(facts!.size).toBe(0);
  });

  it("keeps only id -> canDownload facts and drops entry metadata (no URL/metadata leak)", () => {
    const facts = readStudioRepresentationDownloadability({
      representationDownloads: [
        { id: DOCX_ID, type: "docx", fileName: "a.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document", canDownload: true },
        { id: PDF_ID, type: "pdf", fileName: "gone.pdf", contentType: "application/pdf", canDownload: false },
      ],
    });
    expect(facts!.get(DOCX_ID)).toBe(true);
    expect(facts!.get(PDF_ID)).toBe(false);
    // The map is strictly id -> boolean; nothing else was carried over.
    for (const value of facts!.values()) expect(typeof value).toBe("boolean");
  });

  it("normalises ids and ignores malformed entries instead of guessing", () => {
    const facts = readStudioRepresentationDownloadability({
      representationDownloads: [
        { id: `  ${PDF_ID.toUpperCase()}  `, canDownload: false },
        { id: 42, canDownload: false },
        { id: DOCX_ID, canDownload: "yes" },
        null,
        "not-an-entry",
      ],
    });
    expect(facts!.get(PDF_ID)).toBe(false);
    expect(facts!.size).toBe(1);
  });
});

describe("isStudioRepresentationDownloadable", () => {
  it("keeps everything advertised when no facts were delivered", () => {
    expect(isStudioRepresentationDownloadable(DOCX_ID, null)).toBe(true);
    expect(isStudioRepresentationDownloadable(PDF_ID, null)).toBe(true);
  });

  it("withdraws only an explicit false", () => {
    const facts = new Map<string, boolean>([[PDF_ID, false], [DOCX_ID, true]]);
    expect(isStudioRepresentationDownloadable(PDF_ID, facts)).toBe(false);
    expect(isStudioRepresentationDownloadable(DOCX_ID, facts)).toBe(true);
    // Unknown ids keep today's advertised behaviour; the route stays the source of truth.
    expect(isStudioRepresentationDownloadable("00000000-0000-4000-8000-000000000099", facts)).toBe(true);
    expect(isStudioRepresentationDownloadable(undefined, facts)).toBe(true);
    expect(isStudioRepresentationDownloadable("", facts)).toBe(true);
  });

  it("matches ids across casing and padding", () => {
    const facts = readStudioRepresentationDownloadability({ representationDownloads: [{ id: PDF_ID.toUpperCase(), canDownload: false }] });
    expect(isStudioRepresentationDownloadable(PDF_ID, facts)).toBe(false);
  });
});

describe("filterStudioDownloadableRepresentations", () => {
  it("hides the pruned PDF while retaining the ready DOCX (the #1049 repro)", () => {
    const facts = readStudioRepresentationDownloadability({
      representationDownloads: [
        { id: DOCX_ID, canDownload: true },
        { id: PDF_ID, canDownload: false },
      ],
    });
    const items = snapshot();
    const kept = filterStudioDownloadableRepresentations(items, facts);
    expect(kept.map((item) => item.id)).toEqual([DOCX_ID]);
    // Returned items are the snapshot objects themselves, byte-identical metadata.
    expect(kept[0]).toBe(items[0]);
  });

  it("is a no-op on legacy payloads (facts === null)", () => {
    const items = snapshot();
    expect(filterStudioDownloadableRepresentations(items, null)).toEqual(items);
  });

  it("tolerates absent or non-array snapshots", () => {
    expect(filterStudioDownloadableRepresentations(undefined, null)).toEqual([]);
    expect(filterStudioDownloadableRepresentations(null, null)).toEqual([]);
    expect(filterStudioDownloadableRepresentations(undefined, new Map([[PDF_ID, false]]))).toEqual([]);
  });

  it("is per rendition, never per asset: one pruned PPTX does not hide its sibling", () => {
    const pptxA = "00000000-0000-4000-8000-000000000021";
    const pptxB = "00000000-0000-4000-8000-000000000022";
    const items = [
      { id: pptxA, type: "pptx" },
      { id: pptxB, type: "pptx" },
    ];
    const facts = new Map<string, boolean>([[pptxA, false], [pptxB, true]]);
    expect(filterStudioDownloadableRepresentations(items, facts)).toEqual([items[1]]);
  });
});
