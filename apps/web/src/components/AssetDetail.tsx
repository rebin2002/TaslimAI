/* eslint-disable @next/next/no-img-element -- detail previews use authenticated API URLs with session cookies. */
import { CalendarDays, Download, ExternalLink, FileText, FolderKanban, HardDrive, Headphones, Info, Layers3, Pencil, ShieldCheck, X } from "lucide-react";
import type { Asset } from "../lib/api";
import { assetFileUrl, assetRepresentationUrl } from "../lib/apiBase";
import { AssetTypeIcon, formatAssetSize } from "./AssetCard";

export type AssetDetailLabels = {
  detailEyebrow: string; close: string; structuredTitle: string; structuredDescription: string; download: string; open: string; edit: string; type: string; typeValue: string; created: string; project: string; noProject: string; file: string; notAvailable: string; source: string; manual: string; description: string; representations: string; studios: Record<string, string>;
};

export function AssetDetail({ asset, locale, labels, onClose, onEdit }: {
  asset: Asset;
  locale: string;
  labels: AssetDetailLabels;
  onClose: () => void;
  onEdit: (asset: Asset) => void;
}) {
  const date = new Intl.DateTimeFormat(locale, { dateStyle: "long", timeStyle: "short" }).format(new Date(asset.createdAt));
  const mediaUrl = assetFileUrl(asset.id, true);
  const isImage = asset.canPreview && asset.assetType === "image";
  const isVideo = asset.canPreview && asset.assetType === "video";
  const isAudio = asset.canPreview && (asset.assetType === "audio" || asset.assetType === "music");
  return <div className="modal-backdrop asset-detail-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) onClose(); }}>
    <section className="asset-detail-panel" role="dialog" aria-modal="true" aria-labelledby="asset-detail-title">
      <div className="asset-detail-header"><div className="asset-detail-heading"><span className={`asset-detail-icon is-${asset.assetType}`}><AssetTypeIcon type={asset.assetType} size={25} /></span><div><p className="section-eyebrow">{labels.detailEyebrow}</p><h2 id="asset-detail-title">{asset.name}</h2></div></div><button type="button" className="modal-close" onClick={onClose} aria-label={labels.close}><X size={18} /></button></div>
      <div className="asset-detail-content">
        <div className={`asset-detail-preview is-${asset.assetType}`}>
          {isImage && <img src={mediaUrl} alt={asset.name} />}
          {isVideo && <video src={mediaUrl} controls preload="metadata" aria-label={asset.name} />}
          {isAudio && <div className="asset-detail-audio"><Headphones size={34} /><audio src={mediaUrl} controls preload="metadata" aria-label={asset.name} /></div>}
          {!isImage && !isVideo && !isAudio && <div className="asset-detail-structured"><Layers3 size={33} /><strong>{labels.structuredTitle}</strong><p>{labels.structuredDescription}</p></div>}
        </div>
        <div className="asset-detail-copy">
          <div className="asset-detail-actions">
            {asset.hasFile && <a className="primary-button" href={assetFileUrl(asset.id)}><Download size={15} /> {labels.download}</a>}
            {asset.hasFile && <a className="secondary-button" href={assetFileUrl(asset.id, true)} target="_blank" rel="noreferrer"><ExternalLink size={15} /> {labels.open}</a>}
            <button type="button" className="secondary-button" onClick={() => onEdit(asset)}><Pencil size={15} /> {labels.edit}</button>
          </div>
          <dl className="asset-detail-metadata">
            <div><dt><Info size={14} /> {labels.type}</dt><dd>{labels.typeValue}</dd></div>
            <div><dt><CalendarDays size={14} /> {labels.created}</dt><dd>{date}</dd></div>
            <div><dt><FolderKanban size={14} /> {labels.project}</dt><dd>{asset.projectName || labels.noProject}</dd></div>
            <div><dt><HardDrive size={14} /> {labels.file}</dt><dd>{asset.mimeType || labels.notAvailable} · {formatAssetSize(asset.fileSizeBytes, labels.notAvailable)}</dd></div>
            <div><dt><ShieldCheck size={14} /> {labels.source}</dt><dd>{asset.sourceStudio ? `${labels.studios[asset.sourceStudio] ?? asset.sourceStudio}${asset.sourceJobTitle ? ` · ${asset.sourceJobTitle}` : ""}` : labels.manual}</dd></div>
          </dl>
          {asset.description && <div className="asset-detail-description"><strong>{labels.description}</strong><p>{asset.description}</p></div>}
          {asset.representations.length > 0 && <div className="asset-representations"><div className="asset-subheading"><FileText size={15} /><strong>{labels.representations}</strong></div>{asset.representations.map((representation) => <a key={representation.id} className="asset-representation" href={assetRepresentationUrl(asset.id, representation.id)}><span><strong>{representation.fileName}</strong><small>{representation.representationType.toUpperCase()} · {formatAssetSize(representation.sizeBytes, labels.notAvailable)}</small></span><Download size={15} /></a>)}</div>}
        </div>
      </div>
    </section>
  </div>;
}
