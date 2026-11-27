import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { DocumentReaderBlock } from "./DocumentReaderBlock";

describe("DocumentReaderBlock", () => {
  it("renders a numbered_list block as an ordered list with decimal markers", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock
        block={{ type: "numbered_list", items: ["Confirm the scope", "Collect sources", "Draft the brief"] }}
        previewLabel="Document preview"
      />,
    );
    expect(html).toContain("<ol");
    expect(html).toContain('list-style-type:decimal');
    expect(html).not.toContain("<ul");
    expect(html).toContain("Confirm the scope");
    expect(html).toContain("Collect sources");
    expect(html).toContain("Draft the brief");
  });

  it("recognises a numbered_list type regardless of case or surrounding whitespace", () => {
    for (const type of ["Numbered_List", "NUMBERED_LIST", "  numbered_list  "]) {
      const html = renderToStaticMarkup(
        <DocumentReaderBlock block={{ type, items: ["First", "Second"] }} previewLabel="Document preview" />,
      );
      expect(html).toContain("<ol");
      expect(html).not.toContain("<ul");
    }
  });

  it("keeps a bullet_list block as an unordered list", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "bullet_list", items: ["Alpha", "Beta"] }} previewLabel="Document preview" />,
    );
    expect(html).toContain("<ul");
    expect(html).not.toContain("<ol");
    expect(html).toContain("Alpha");
    expect(html).toContain("Beta");
  });

  it("falls back to an unordered list for an unknown block type with items", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "checklist", items: ["One thing"] }} previewLabel="Document preview" />,
    );
    expect(html).toContain("<ul");
    expect(html).not.toContain("<ol");
  });

  it("renders duplicated reference items as separate entries without dropping any", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "numbered_list", items: ["See appendix", "See appendix", "See glossary"] }} previewLabel="Document preview" />,
    );
    expect((html.match(/<li>/g) ?? []).length).toBe(3);
    expect(html).toContain("See glossary");
  });

  it("omits the list entirely when items are missing or empty", () => {
    expect(renderToStaticMarkup(<DocumentReaderBlock block={{ type: "numbered_list" }} previewLabel="Document preview" />)).not.toContain("<ol");
    expect(renderToStaticMarkup(<DocumentReaderBlock block={{ type: "numbered_list", items: [] }} previewLabel="Document preview" />)).not.toContain("<ol");
    const empty = renderToStaticMarkup(<DocumentReaderBlock block={{ type: "bullet_list", items: [] }} previewLabel="Document preview" />);
    expect(empty).not.toContain("<ul");
    expect(empty).not.toContain("<ol");
    expect(empty).not.toContain("<li");
  });

  it("renders a paragraph block as plain text without any list markup", () => {
    const html = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "paragraph", text: "Quarterly figures were stable." }} previewLabel="Document preview" />,
    );
    expect(html).not.toContain("<ul");
    expect(html).not.toContain("<ol");
    expect(html).toContain("Quarterly figures were stable.");
  });

  it("renders heading blocks with heading semantics and tables through the preview table", () => {
    const heading = renderToStaticMarkup(<DocumentReaderBlock block={{ type: "heading", text: "References" }} previewLabel="Document preview" />);
    expect(heading).toContain("<p>References</p>");
    expect(heading).not.toContain("<ol");
    expect(heading).not.toContain("<ul");
    const table = renderToStaticMarkup(
      <DocumentReaderBlock block={{ type: "table", rows: [{ cells: ["Source", "Year"] }, { cells: ["Registry", "2026"] }] }} previewLabel="Document preview" />,
    );
    expect(table).toContain('role="table"');
    expect(table).toContain('aria-label="Document preview"');
    expect(table).toContain("Registry");
  });
});
