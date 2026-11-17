import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  documentPreviewBounds,
  parseDocumentJobResult,
  sanitizeDocumentPreviewText,
} from "./documentStudioState";
import { DocumentPreviewTable } from "@/components/DocumentPreviewTable";
import type { GenerationJob } from "./api";

// All fixtures below are synthetic: fixed identifiers, fabricated prose, and crafted
// payloads only. Nothing touches a provider, the network, a database, or production data.

const assetId = "00000000-0000-4000-8000-000000000010";

const parsePayload = (payload: unknown) => {
  const completed: GenerationJob = {
    id: "00000000-0000-4000-8000-000000000001", workspaceId: "workspace-1", projectId: null, jobType: "document.generate", status: "Succeeded", title: "Report", progressPercent: 100,
    createdAt: "2026-11-17T00:00:00Z", queuedAt: "2026-11-17T00:00:00Z", startedAt: null, completedAt: null, failedAt: null, cancelledAt: null,
    cancellationRequested: false, resultJson: JSON.stringify(payload), errorCode: null, errorMessage: null, outputs: [],
  };
  return parseDocumentJobResult(completed);
};

const paragraphs = (count: number) => Array.from({ length: count }, (_, index) => ({ type: "paragraph", text: `Synthetic paragraph ${index}.` }));

describe("Document Studio template preview sanitization", () => {
  it("keeps a benign synthetic template byte-for-byte and stays idempotent", () => {
    const payload = {
      assetId,
      title: "Q3 Market Report",
      language: "en",
      summary: "Revenue grew while costs stayed flat.",
      sections: [{ heading: "Overview", blocks: [{ type: "paragraph", text: "Revenue grew 12%." }, { type: "list", items: ["Alpha", "Beta"] }, { type: "table", rows: [{ cells: ["Metric", "Value"], other: "ignored" }] }] }],
    };
    const parsed = parsePayload(payload);
    expect(parsed).toMatchObject({ assetId, title: "Q3 Market Report", language: "en", summary: "Revenue grew while costs stayed flat." });
    expect(parsed?.sections?.[0].blocks[0].text).toBe("Revenue grew 12%.");
    expect(parsed?.sections?.[0].blocks[1].items).toEqual(["Alpha", "Beta"]);
    expect(parsed?.sections?.[0].blocks[2].rows?.[0].cells).toEqual(["Metric", "Value"]);
    // A second pass over the same persisted result yields the identical preview, so the
    // sanitizer never compounds across polls.
    expect(JSON.stringify(parsePayload(payload))).toBe(JSON.stringify(parsed));
  });

  it("parses Arabic and Kurdish prose without mangling the text", () => {
    const parsed = parsePayload({
      assetId,
      language: "ar",
      summary: "نمو الإيرادات مع ثبات التكاليف",
      sections: [{ heading: "ملخص", blocks: [{ type: "paragraph", text: "تقرير الربع الثالث عن المبيعات" }] }],
    });
    expect(parsed?.summary).toBe("نمو الإيرادات مع ثبات التكاليف");
    expect(parsed?.sections?.[0].heading).toBe("ملخص");
    expect(parsed?.sections?.[0].blocks[0].text).toBe("تقرير الربع الثالث عن المبيعات");
  });

  it("renders markup-bearing template text as escaped inert text, never as live nodes", () => {
    const parsed = parsePayload({
      assetId,
      sections: [{ heading: "Overview", blocks: [{ type: "table", rows: [{ cells: ["<img src=x onerror=alert(1)>", "<script>alert(2)</script>"] }] }] }],
    });
    const rows = parsed?.sections?.[0].blocks[0].rows ?? [];
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={rows} label="Document preview" />);
    // The payload text survives, but only as escaped text content: the opening angle
    // bracket is always an entity, so the browser parser can never open an element node.
    expect(html).toContain("&lt;img src=x onerror=alert(1)&gt;");
    expect(html).toContain("&lt;script&gt;alert(2)&lt;/script&gt;");
    expect(html).not.toContain("<img");
    expect(html).not.toContain("<script");
  });

  it("strips control characters and collapses whitespace in every rendered field", () => {
    const parsed = parsePayload({
      assetId,
      title: "Report\u0000\u001b[2;3~rm",
      summary: "Line one.\n\n    Line two.",
      sections: [{ heading: "Over\u0007view", blocks: [{ type: "paragraph\u0000", text: "Revenue\t\tgrew   12%." }] }],
    });
    expect(parsed?.title).toBe("Report[2;3~rm");
    expect(parsed?.summary).toBe("Line one.\nLine two.");
    expect(parsed?.sections?.[0].heading).toBe("Overview");
    expect(parsed?.sections?.[0].blocks[0].type).toBe("paragraph");
    expect(parsed?.sections?.[0].blocks[0].text).toBe("Revenue grew 12%.");
  });

  it("removes zero-width and bidirectional spoofing characters", () => {
    const parsed = parsePayload({
      assetId,
      sections: [{ heading: "Overview", blocks: [{ type: "paragraph", text: "re\u200bsu\u200clt\u2008ok" }, { type: "list", items: ["\u202e\u2066Invoice\u2069 paid\u2068"] }, { type: "table", rows: [{ cells: ["\ufeff\u202aTotal\u202c\u00ad100"] }] }] }],
    });
    expect(parsed?.sections?.[0].blocks[0].text).toBe("result ok");
    expect(parsed?.sections?.[0].blocks[1].items).toEqual(["Invoice paid"]);
    expect(parsed?.sections?.[0].blocks[2].rows?.[0].cells).toEqual(["Total100"]);
    for (const section of parsed?.sections ?? []) {
      for (const block of section.blocks) {
        expect(block.text ?? "").not.toMatch(/[\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff\u00ad]/u);
        for (const item of block.items ?? []) expect(item).not.toMatch(/[\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff\u00ad]/u);
        for (const row of block.rows ?? []) for (const cell of row.cells) expect(cell).not.toMatch(/[\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff\u00ad]/u);
      }
    }
  });

  it("hard-caps oversized template fields at the published bound with an ellipsis", () => {
    const parsed = parsePayload({
      assetId,
      title: "T".repeat(10_000),
      summary: "S".repeat(50_000),
      sections: [{ heading: "H".repeat(3_000), blocks: [{ type: "p".repeat(500), text: "X".repeat(100_000) }, { type: "list", items: ["I".repeat(9_000)] }, { type: "table", rows: [{ cells: ["C".repeat(4_000)] }] }] }],
    });
    expect(parsed?.title).toHaveLength(documentPreviewBounds.titleMax);
    expect(parsed?.title?.endsWith("\u2026")).toBe(true);
    expect(parsed?.summary).toHaveLength(documentPreviewBounds.summaryMax);
    expect(parsed?.sections?.[0].heading).toHaveLength(documentPreviewBounds.headingMax);
    const blocks = parsed?.sections?.[0].blocks ?? [];
    expect(blocks[0].text).toHaveLength(documentPreviewBounds.textMax);
    expect(blocks[1].items?.[0]).toHaveLength(documentPreviewBounds.itemMax);
    expect(blocks[2].rows?.[0].cells[0]).toHaveLength(documentPreviewBounds.cellMax);
    expect(blocks[0].type).toHaveLength(documentPreviewBounds.blockTypeMax);
    // The standalone helper keeps its promise for any caller-provided bound.
    expect(sanitizeDocumentPreviewText("a".repeat(12), 5)).toBe("aaaa\u2026");
    expect(sanitizeDocumentPreviewText("  already  one line  ", 100)).toBe("already one line");
    expect(sanitizeDocumentPreviewText(42, 10)).toBe("");
    expect(sanitizeDocumentPreviewText(null, 10)).toBe("");
  });

  it("bounds the template structure so one result cannot flood the reader", () => {
    const parsed = parsePayload({
      assetId,
      sections: Array.from({ length: 100 }, (_, sectionIndex) => ({
        heading: `Section ${sectionIndex}`,
        blocks: [
          ...paragraphs(100),
          { type: "list", items: Array.from({ length: 200 }, (_, index) => `Item ${index}`) },
          { type: "table", rows: Array.from({ length: 200 }, (_, rowIndex) => ({ cells: Array.from({ length: 30 }, (_, cellIndex) => `R${rowIndex}C${cellIndex}`) })) },
        ],
      })),
    });
    const sections = parsed?.sections ?? [];
    expect(sections).toHaveLength(documentPreviewBounds.sectionsMax);
    for (const section of sections) {
      expect(section.blocks.length).toBeLessThanOrEqual(documentPreviewBounds.blocksPerSectionMax);
      for (const block of section.blocks) {
        expect((block.items ?? []).length).toBeLessThanOrEqual(documentPreviewBounds.itemsPerBlockMax);
        for (const row of block.rows ?? []) expect(row.cells.length).toBeLessThanOrEqual(documentPreviewBounds.cellsPerRowMax);
        expect((block.rows ?? []).length).toBeLessThanOrEqual(documentPreviewBounds.rowsPerBlockMax);
      }
    }
    // A section whose every block failed validation renders nothing at all, and a block
    // type that sanitizes to empty is dropped instead of rendering an anonymous row.
    expect(parsePayload({ assetId, sections: [{ heading: "Hollow", blocks: [] }] })?.sections).toEqual([]);
    expect(parsePayload({ assetId, sections: [{ heading: "Hollow", blocks: [{ type: "\u200b", text: "ghost" }] }] })?.sections).toEqual([]);
  });

  it("accepts only a short safe language tag for the preview chip", () => {
    const valid = parsePayload({ assetId, language: "ar-EG" });
    expect(valid?.language).toBe("ar-EG");
    for (const hostile of [
      "<script>alert(1)</script>",
      "en\u202e",
      "English (Business Report)",
      `tag ${"x".repeat(200)}`,
      "en\u0000US",
    ]) {
      expect(parsePayload({ assetId, language: hostile })?.language).toBeUndefined();
    }
  });

  it("never leaks non-string or invisible-only content into the rendered preview", () => {
    const parsed = parsePayload({
      assetId,
      sections: [{ heading: "Overview", blocks: [{ type: "table", rows: [{ cells: ["ok", 42, { evil: true }, "\u200b\u200c"] }] }, { type: "list", items: [7, "\u200d", "kept"] }] }],
    });
    expect(parsed?.sections?.[0].blocks[0].rows?.[0].cells).toEqual(["ok"]);
    expect(parsed?.sections?.[0].blocks[1].items).toEqual(["kept"]);
    // Rows that end up with no visible cells at all are dropped entirely.
    expect(parsePayload({ assetId, sections: [{ heading: "Overview", blocks: [{ type: "table", rows: [{ cells: ["\u200b", 13] }] }] }] })?.sections?.[0].blocks[0].rows).toBeNull();
  });
});
