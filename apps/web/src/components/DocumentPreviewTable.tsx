type DocumentPreviewTableProps = Readonly<{
  rows: ReadonlyArray<{ cells: ReadonlyArray<string> }>;
  label: string;
}>;
export function DocumentPreviewTable({ rows, label }: DocumentPreviewTableProps) {
  return <div className="document-preview-table" role="table" aria-label={label}>{rows.map((row, rowIndex) => <div className="document-preview-row" key={rowIndex} role="row">{row.cells.map((cell, cellIndex) => <span key={`${rowIndex}-${cellIndex}`} role="cell">{cell}</span>)}</div>)}</div>;
}
