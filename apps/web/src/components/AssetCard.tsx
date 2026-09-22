import { Archive, Download, File, FileText, Image as ImageIcon, Music2, Pencil, Presentation, RotateCcw, Share2, Video, Volume2 } from "lucide-react";
import type { Asset, AssetType } from "../lib/api";
import { assetFileUrl } from "../lib/apiBase";

function AssetTypeIcon({ type, size = 22 }: { type: AssetType; size?: number }) {
  const props = { size, strokeWidth: 1.8 };
  if (type === "image") return <ImageIcon {...props} />;
  if (type === "document" || type === "research") return <FileText {...props} />;
  if (type === "presentation") return <Presentation {...props} />;
  if (type === "video") return <Video {...props} />;
  if (type === "audio") return <Volume2 {...props} />;
  if (type === "music") return <Music2 {...props} />;
  if (type === "social") return <Share2 {...props} />;
  return <File {...props} />;
}

export type AssetCardLabels = {
  project: string;
  workspace: string;
  rename: string;
  archive: string;
  restore: string;
  download: string;
  type: string;
};

export function AssetCard({ asset, labels, locale, onEdit, onArchive, onRestore }: {
  asset: Asset;
  labels: AssetCardLabels;
  locale: string;
  onEdit: (asset: Asset) => void;
  onArchive: (asset: Asset) => void;
  onRestore: (asset: Asset) => void;
}) {
  const fileUrl = assetFileUrl(asset.id);
  return <article className="asset-card">
    <a className={`asset-preview is-${asset.assetType}`} href={asset.hasFile ? fileUrl : undefined} aria-label={asset.name}>
      {asset.canPreview ? <span className="asset-image-preview" style={{ backgroundImage: `url(${assetFileUrl(asset.id, true)})` }} role="img" aria-label={asset.name} /> : <span className="asset-type-icon"><AssetTypeIcon type={asset.assetType} size={28} /></span>}
      <span className="asset-type-badge">{labels.type}</span>
    </a>
    <div className="asset-card-body">
      <div><h2>{asset.name}</h2><p>{asset.description || (asset.projectName ? `${labels.project}: ${asset.projectName}` : labels.workspace)}</p></div>
      <div className="asset-card-meta"><span>{new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(asset.createdAt))}</span>{asset.projectName && <span>{asset.projectName}</span>}</div>
      <div className="asset-card-actions">
        <button className="asset-action" onClick={() => onEdit(asset)}><Pencil size={14} /> {labels.rename}</button>
        {asset.hasFile && <a className="asset-action" href={fileUrl}><Download size={14} /> {labels.download}</a>}
        {asset.status === "Active" ? <button className="asset-action is-danger" onClick={() => onArchive(asset)}><Archive size={14} /> {labels.archive}</button> : <button className="asset-action" onClick={() => onRestore(asset)}><RotateCcw size={14} /> {labels.restore}</button>}
      </div>
    </div>
  </article>;
}
