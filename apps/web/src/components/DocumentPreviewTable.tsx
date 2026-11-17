type DocumentPreviewTableProps = Readonly<{
  rows: ReadonlyArray<{ cells: ReadonlyArray<string> }>;
  label: string;
}>;

/**
 * A preview body is trimmed to fit the persisted job result, so a table cell can reach the reader
 * as an empty or whitespace-only remnant. `trim()` also folds the no-break and em spaces such a
 * cut leaves behind, none of which are content the reader can show.
 */
export function hasRenderablePreviewText(value: string | null | undefined): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isRenderableRow(row: { cells: ReadonlyArray<string> }) {
  return row.cells.some(hasRenderablePreviewText);
}

export function DocumentPreviewTable({ rows, label }: DocumentPreviewTableProps) {
  // A row whose every cell is blank paints a bordered strip with nothing in it, and a table made
  // only of such rows announces an empty role="table" region under the preview label, so the
  // whole table falls back to rendering nothing rather than to empty structure. Cells inside a
  // row that does carry content are all kept, so the columns stay aligned with the document.
  const renderableRows = rows.filter(isRenderableRow);
  if (renderableRows.length === 0) return null;
  return <div className="document-preview-table" role="table" aria-label={label}>{renderableRows.map((row, rowIndex) => <div className="document-preview-row" key={rowIndex} role="row">{row.cells.map((cell, cellIndex) => <span key={`${rowIndex}-${cellIndex}`} role="cell">{cell}</span>)}</div>)}</div>;
}
