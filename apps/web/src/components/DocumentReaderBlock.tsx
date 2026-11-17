import { DocumentPreviewTable } from "./DocumentPreviewTable";

export type DocumentReaderBlockModel = Readonly<{
  type: string;
  text?: string | null;
  items?: readonly string[] | null;
  rows?: readonly { cells: readonly string[] }[] | null;
}>;

type DocumentReaderBlockProps = Readonly<{
  block: DocumentReaderBlockModel;
  previewLabel: string;
}>;

function isNumberedBlock(block: DocumentReaderBlockModel) {
  return block.type.trim().toLowerCase() === "numbered_list";
}

export function DocumentReaderBlock({ block, previewLabel }: DocumentReaderBlockProps) {
  const items = block.items ?? [];
  const rows = block.rows ?? [];
  return <>
    {block.text ? <p>{block.text}</p> : null}
    {items.length > 0 ? (isNumberedBlock(block)
      ? <ol style={{ listStyleType: "decimal" }}>{items.map((item, itemIndex) => <li key={itemIndex}>{item}</li>)}</ol>
      : <ul>{items.map((item, itemIndex) => <li key={itemIndex}>{item}</li>)}</ul>) : null}
    {rows.length > 0 ? <DocumentPreviewTable rows={rows} label={previewLabel} /> : null}
  </>;
}
