/* eslint-disable @next/next/no-img-element -- previews use authenticated API URLs with session cookies. */
import { Archive, AudioLines, Download, File, FileText, Image as ImageIcon, Music2, Pencil, Play, Presentation, RotateCcw, Share2, Sparkles, Video, Volume2 } from "lucide-react";
import type { Asset, AssetType } from "../lib/api";
import { assetFileUrl } from "../lib/apiBase";

export function AssetTypeIcon({ type, size = 22 }: { type: AssetType; size?: number }) {
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
  open: string;
  type: string;
  fileUnavailable: string;
};

function extension(asset: Asset) {
  const fromName = asset.name.split(".").pop();
  return (fromName && fromName !== asset.name ? fromName : asset.mimeType?.split("/").pop() ?? "asset").toUpperCase();
}

function textSnippet(asset: Asset) {
  return asset.description?.trim() || `${asset.name} · ${extension(asset)}`;
}

function MediaPreview({ asset, labels, onOpen }: { asset: Asset; labels: AssetCardLabels; onOpen: () => void }) {
  if (!asset.hasFile) return <div className="asset-structured-preview"><AssetTypeIcon type={asset.assetType} size={30} /><span>{labels.fileUnavailable}</span></div>;
  if (asset.canPreview && asset.assetType === "image") return <img className="asset-image-preview" src={assetFileUrl(asset.id, true)} alt="" loading="lazy" />;
  if (asset.canPreview && asset.assetType === "video") return <button type="button" className="asset-video-preview" onClick={onOpen} aria-label={`${labels.open}: ${asset.name}`}><video src={assetFileUrl(asset.id, true)} muted playsInline preload="metadata" aria-hidden="true" /><span className="asset-media-play"><Play size={14} fill="currentColor" /></span></button>;
  if (asset.assetType === "audio" || asset.assetType === "music") return <div className="asset-audio-preview" onClick={event => event.stopPropagation()} onPointerDown={event => event.stopPropagation()}><div className="asset-audio-mark"><AudioLines size={24} /></div><div className="asset-waveform" aria-hidden="true"><i /><i /><i /><i /><i /><i /><i /><i /><i /><i /><i /></div><audio src={assetFileUrl(asset.id, true)} controls preload="metadata" aria-label={asset.name} /></div>;
  if (asset.assetType === "presentation") return <button type="button" className="asset-slide-preview" onClick={onOpen} aria-label={`${labels.open}: ${asset.name}`}><span className="asset-slide-top"><Presentation size={17} /><em>{extension(asset)}</em></span><strong>{asset.name}</strong><span>{textSnippet(asset)}</span><small><i /><i /><i /></small></button>;
  if (asset.assetType === "document" || asset.assetType === "research" || asset.assetType === "social") return <button type="button" className={`asset-document-preview is-${asset.assetType}`} onClick={onOpen} aria-label={`${labels.open}: ${asset.name}`}><span className="asset-document-top"><AssetTypeIcon type={asset.assetType} size={17} /><em>{extension(asset)}</em></span><strong>{asset.name}</strong><span>{textSnippet(asset)}</span><small><i /><i /><i /><i /></small></button>;
  return <button type="button" className="asset-open-preview" onClick={onOpen} aria-label={`${labels.open}: ${asset.name}`}><span className="asset-type-icon"><AssetTypeIcon type={asset.assetType} size={28} /></span><span className="asset-preview-play"><Sparkles size={14} /></span></button>;
}

export function AssetCard({ asset, labels, locale, onOpen, onEdit, onArchive, onRestore }: {
  asset: Asset;
  labels: AssetCardLabels;
  locale: string;
  onOpen: (asset: Asset) => void;
  onEdit: (asset: Asset) => void;
  onArchive: (asset: Asset) => void;
  onRestore: (asset: Asset) => void;
}) {
  const fileUrl = assetFileUrl(asset.id);
  const date = new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(new Date(asset.createdAt));
  return <article className={`asset-card is-${asset.assetType}`}>
    <div className={`asset-preview is-${asset.assetType}`}>
      <MediaPreview asset={asset} labels={labels} onOpen={() => onOpen(asset)} />
      {asset.hasFile && asset.assetType === "image" && <button type="button" className="asset-preview-hit-area" onClick={() => onOpen(asset)} aria-label={`${labels.open}: ${asset.name}`} />}
      <span className="asset-type-badge">{labels.type}</span>
    </div>
    <div className="asset-card-body">
      <div className="asset-card-heading"><button type="button" className="asset-card-title" onClick={() => onOpen(asset)}><h2>{asset.name}</h2></button><span className="asset-card-status">{asset.status === "Active" ? "●" : "○"}</span></div>
      <p className="asset-card-description">{asset.description || (asset.projectName ? `${labels.project}: ${asset.projectName}` : labels.workspace)}</p>
      <div className="asset-card-meta"><span>{date}</span>{asset.projectName && <span className="asset-card-project">{asset.projectName}</span>}</div>
      <div className="asset-card-actions">
        <button type="button" className="asset-action" onClick={() => onEdit(asset)}><Pencil size={14} /> {labels.rename}</button>
        {asset.hasFile ? <a className="asset-action" href={fileUrl}><Download size={14} /> {labels.download}</a> : <span className="asset-unavailable">{labels.fileUnavailable}</span>}
        {asset.status === "Active" ? <button type="button" className="asset-action is-danger" onClick={() => onArchive(asset)}><Archive size={14} /> {labels.archive}</button> : <button type="button" className="asset-action" onClick={() => onRestore(asset)}><RotateCcw size={14} /> {labels.restore}</button>}
      </div>
    </div>
  </article>;
}

export function formatAssetSize(bytes: number | null, unknownLabel = "—") {
  if (bytes === null || !Number.isFinite(bytes)) return unknownLabel;
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index += 1; }
  return `${value.toFixed(index === 0 || value >= 10 ? 0 : 1)} ${units[index]}`;
}
