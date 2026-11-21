/**
 * Provider-free pins for the generic downloadable-document metadata lane.
 *
 * Scope: how EN/AR/Sorani timestamps render on the Document Studio download
 * surface, and how EN/AR/Sorani download names survive the client-side
 * representation gate. These tests never touch storage identity, asset ids or
 * database mappings: the pinned contract is that identity fields pass through
 * untouched and only presentation is formatted.
 */
import { describe, expect, it } from "vitest";
import { formatAssetDate } from "./DocumentStudioView";
import { isSafeDocumentRepresentation, parseDocumentJobResult } from "@/lib/documentStudioState";
import { localeTag } from "@/lib/i18n";
import type { GenerationJob } from "@/lib/api";

// Midday UTC on a fixed day: every host timezone within UTC±12 still shows the
// 16th, so the pins below are independent of the machine running them.
const pinnedInstant = "2026-11-16T12:31:00Z";
const pinnedOptions = { month: "short", day: "numeric", year: "numeric" } as const;

const documentAssetId = "00000000-0000-4000-8000-000000000010";
const representationId = "00000000-0000-4000-8000-000000000011";
const docxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

const succeededJob = (representations: unknown[]): GenerationJob => ({
  id: "00000000-0000-4000-8000-000000000001",
  workspaceId: "workspace-1",
  projectId: null,
  jobType: "document.generate",
  status: "Succeeded",
  title: "Report",
  progressPercent: 100,
  createdAt: pinnedInstant,
  queuedAt: pinnedInstant,
  startedAt: null,
  completedAt: pinnedInstant,
  failedAt: null,
  cancelledAt: null,
  cancellationRequested: false,
  resultJson: JSON.stringify({
    assetId: documentAssetId,
    title: "Report",
    summary: "Summary",
    sections: [],
    representations,
  }),
  errorCode: null,
  errorMessage: null,
  outputs: [],
});

describe("document download rail timestamps", () => {
  it("falls back to the raw value instead of throwing for invalid instants", () => {
    // Regression: Intl.DateTimeFormat.format(new Date(NaN)) raises RangeError, and a
    // throwing render would take the Document Studio download surface down with it.
    for (const locale of ["en", "ar", "ku"] as const) {
      expect(() => formatAssetDate("not-a-timestamp", locale)).not.toThrow();
      expect(formatAssetDate("not-a-timestamp", locale)).toBe("not-a-timestamp");
      expect(formatAssetDate("", locale)).toBe("");
    }
  });

  it("pins the English rendering of the fixed instant", () => {
    // Day 15-17 tolerated so the pin holds on any host timezone; month, year and the
    // "Mon D, YYYY" shape with ASCII digits are exact.
    expect(formatAssetDate(pinnedInstant, "en")).toMatch(/^Nov 1[567], 2026$/);
  });

  it("pins the Arabic rendering to the canonical Arabic tag", () => {
    const formatted = formatAssetDate(pinnedInstant, "ar");
    expect(formatted).toBe(new Intl.DateTimeFormat(localeTag("ar"), pinnedOptions).format(new Date(pinnedInstant)));
    expect(formatted).toContain("نوفمبر");
    expect(formatted).not.toMatch(/[A-Za-z]/);
  });

  it("pins the Sorani rendering to ckb, never to Latin Kurmanji", () => {
    expect(localeTag("ku")).toBe("ckb");
    const formatted = formatAssetDate(pinnedInstant, "ku");
    expect(formatted).toBe(new Intl.DateTimeFormat("ckb", pinnedOptions).format(new Date(pinnedInstant)));
    // Sorani renders with Arabic-Indic numerals and Arabic-script month words; the
    // raw UI locale "ku" would silently produce Latin Kurmanji ("16 mjd, 2026").
    expect(formatted).toMatch(/[\u0660-\u0669]/);
    expect(formatted).not.toMatch(/[A-Za-z]/);
    expect(formatted).not.toBe(new Intl.DateTimeFormat("ku", pinnedOptions).format(new Date(pinnedInstant)));
  });
});

describe("document download representation names", () => {
  const localizedNames: ReadonlyArray<{ type: string; contentType: string; fileName: string }> = [
    { type: "docx", contentType: docxContentType, fileName: "Quarterly report.docx" },
    { type: "pdf", contentType: "application/pdf", fileName: "تقرير الربع الأول.pdf" },
    { type: "docx", contentType: docxContentType, fileName: "کورتەی ڕاپۆرتی.docx" },
  ];

  it("keeps English, Arabic and Sorani download names byte-identical and identity untouched", () => {
    for (const name of localizedNames) {
      const representation = { id: representationId, ...name, fileName: ` ${name.fileName} ` };
      expect(isSafeDocumentRepresentation(representation)).toBe(true);
      const parsed = parseDocumentJobResult(succeededJob([representation]));
      // Only surrounding whitespace is trimmed; the stored script, separators-free
      // body and the asset/representation ids pass through unchanged.
      expect(parsed?.representations).toEqual([{ id: representationId, ...name }]);
    }
  });

  it("drops localized names carrying separators or control characters", () => {
    const forbiddenNames = [
      "تقرير/ربع.pdf",
      "کورتەی\\ڕاپۆرت.docx",
      "ت\u0001قرير.pdf",
    ];
    for (const fileName of forbiddenNames) {
      const type = fileName.endsWith("docx") ? "docx" : "pdf";
      const contentType = type === "docx" ? docxContentType : "application/pdf";
      expect(isSafeDocumentRepresentation({ id: representationId, type, fileName, contentType })).toBe(false);
      expect(parseDocumentJobResult(succeededJob([{ id: representationId, type, fileName, contentType }]))?.representations).toEqual([]);
    }
  });

  it("applies the same 255-character limit to Arabic-script names as to Latin ones", () => {
    // "ت".repeat(251) + ".pdf" is exactly 255 UTF-16 code units, matching the
    // server-side DocumentFileName gate that counts the same units.
    const atLimit = `${"ت".repeat(251)}.pdf`;
    const overLimit = `${"ت".repeat(252)}.pdf`;
    expect(isSafeDocumentRepresentation({ id: representationId, type: "pdf", fileName: atLimit, contentType: "application/pdf" })).toBe(true);
    expect(isSafeDocumentRepresentation({ id: representationId, type: "pdf", fileName: overLimit, contentType: "application/pdf" })).toBe(false);
  });
});
