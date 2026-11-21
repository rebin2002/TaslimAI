import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { DocumentPreviewTable } from "./DocumentPreviewTable";
import { DocumentReaderBlock } from "./DocumentReaderBlock";

/**
 * Document Studio preview text rendering, graceful fallback.
 *
 * The canonical preview that the reader renders is the job result that survived
 * DocumentGenerationContractMapper.SerializeResult: when the preview does not fit the persisted
 * column, every text field is cut to the largest measured scale, so a block's text or a table
 * cell can arrive in the web reader as an empty or whitespace-only remnant ("\n ", " ").
 * The reader used to test only for the presence of those strings, so a remnant painted a blank
 * serif paragraph with the pre-wrap styling of .document-reader-block p, and a table whose cells
 * were all blank still painted the bordered table shell and announced an empty
 * role="table" aria-label="Document preview" region to a screen reader.
 *
 * Provider-free and network-free: the components are rendered from literal block models with
 * renderToStaticMarkup, so no API, storage, worker, or translation catalog is involved.
 */
describe("Document Studio preview text rendering graceful fallback", () => {
  it("renders real paragraph text untouched", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "paragraph", text: "Revenue held at 12% for the quarter." }} previewLabel="Document preview" />,
    );
    expect(html).toContain("<p>Revenue held at 12% for the quarter.</p>");
  });

  it("renders a table whose cells carry content", () => {
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: ["Region", "Revenue"] }, { cells: ["Kurdistan", "12%"] }]} label="Document preview" />);
    expect(html).toContain('role="table"');
    expect((html.match(/role="row"/g) ?? []).length).toBe(2);
    expect(html).toContain("Kurdistan");
  });

  it("drops a paragraph whose text is only whitespace after the preview was trimmed", () => {
    for (const text of [" ", "\n", " \n\t ", "\u00a0"]) {
      const html = renderToStaticMarkup(<DocumentReaderBlock block={{ type: "paragraph", text }} previewLabel="Document preview" />);
      expect(html).not.toContain("<p>");
    }
  });

  it("drops a heading whose text is only whitespace instead of painting a blank line", () => {
    const html = renderToStaticMarkup(<DocumentReaderBlock block={{ type: "heading", text: "  " }} previewLabel="Document preview" />);
    expect(html).toBe("");
  });

  it("keeps the surrounding content of a block whose text remnant is blank", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "bullet_list", text: " ", items: ["Confirm the scope"] }} previewLabel="Document preview" />,
    );
    expect(html).not.toContain("<p>");
    expect(html).toContain("<li>Confirm the scope</li>");
  });

  it("omits a table row whose cells are all blank", () => {
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: [" ", ""] }, { cells: ["Registry", "2026"] }]} label="Document preview" />);
    expect((html.match(/role="row"/g) ?? []).length).toBe(1);
    expect(html).toContain("Registry");
  });

  it("falls back to no table at all when every cell of every row is blank", () => {
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: ["", " "] }, { cells: ["\n"] }]} label="Document preview" />);
    expect(html).toBe("");
    expect(html).not.toContain('role="table"');
  });

  it("renders nothing for a table block whose whole preview body was trimmed away", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "table", rows: [{ cells: [" "] }, { cells: ["", "\n"] }] }} previewLabel="Document preview" />,
    );
    expect(html).not.toContain('role="table"');
    expect(html).not.toContain("document-preview-row");
  });

  it("keeps a real cell that sits beside a blank one in the same row", () => {
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: ["", "Q4", " "] }]} label="Document preview" />);
    expect((html.match(/role="row"/g) ?? []).length).toBe(1);
    expect((html.match(/role="cell"/g) ?? []).length).toBe(3);
    expect(html).toContain("Q4");
  });

  it("treats a non-breaking-space cell as blank text rather than content", () => {
    expect(renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: ["\u00a0", "\u2003"] }]} label="Document preview" />)).toBe("");
  });

  it("does not let the blank fallback swallow the empty-state copy of a rendered document", () => {
    const sections = [
      { heading: "Overview", blocks: [{ type: "paragraph", text: " ", items: null, rows: null }] },
      { heading: "Detail", blocks: [{ type: "table", text: null, items: null, rows: [{ cells: ["", ""] }] }] },
    ];
    const html = sections.map((section) => section.blocks
      .map((block) => renderToStaticMarkup(<DocumentReaderBlock block={block} previewLabel="Document preview" />))).flat().join("");
    expect(html).toBe("");
  });
});
