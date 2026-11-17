import { localeTag } from "@/lib/i18n";
/* eslint-disable @next/next/no-img-element -- detail previews use authenticated API URLs with session cookies. */
import { useEffect, useRef, type KeyboardEvent } from "react";
import { CalendarDays, Download, ExternalLink, FileText, FolderKanban, HardDrive, Headphones, Info, Layers3, Pencil, ShieldCheck, X } from "lucide-react";
import type { Asset } from "../lib/api";
import { assetFileUrl, assetRepresentationUrl } from "../lib/apiBase";
import { AssetTypeIcon, formatAssetSize } from "./AssetCard";

export type AssetDetailLabels = {
  detailEyebrow: string; close: string; structuredTitle: string; structuredDescription: string; previewLabel: string; summaryLabel: string; download: string; open: string; edit: string; type: string; typeValue: string; created: string; project: string; noProject: string; file: string; notAvailable: string; source: string; manual: string; description: string; representations: string; studios: Record<string, string>;
};

function detailDocumentPreview(asset: Asset, labels: AssetDetailLabels) {
  const isPresentation = asset.assetType === "presentation";
  const isResearch = asset.assetType === "research";
  return <div className={`asset-detail-document is-${asset.assetType}`}>
    <div className="asset-detail-document-top"><span><AssetTypeIcon type={asset.assetType} size={18} /></span><small>{asset.mimeType || asset.assetType.toUpperCase()}</small></div>
    <h3>{asset.name}</h3>
    <div className="asset-detail-rule" />
    <p>{asset.description || labels.structuredDescription}</p>
    <div className="asset-detail-lines"><i /><i /><i /><i /><i /></div>
    <strong>{isPresentation ? "01" : isResearch ? labels.summaryLabel : labels.previewLabel}</strong>
  </div>;
}

/**
 * The interactive descendants of the panel, in document order. Walked by hand from the panel
 * root so the tab ring never depends on what happens to be focusable outside the dialog.
 */
function collectFocusable(panel: HTMLElement): HTMLElement[] {
  const focusable: HTMLElement[] = [];
  const visit = (node: Element) => {
    const tag = node.tagName.toUpperCase();
    if (tag === "BUTTON" || tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA") {
      if (node.getAttribute("disabled") === null) focusable.push(node as HTMLElement);
    } else if (tag === "A") {
      if (node.getAttribute("href") !== null) focusable.push(node as HTMLElement);
    } else if (tag === "AUDIO" || tag === "VIDEO") {
      if (node.getAttribute("controls") !== null) focusable.push(node as HTMLElement);
    }
    for (const child of Array.from(node.children)) visit(child);
  };
  visit(panel);
  return focusable;
}

export function AssetDetail({ asset, locale, labels, onClose, onEdit }: {
  asset: Asset;
  locale: string;
  labels: AssetDetailLabels;
  onClose: () => void;
  onEdit: (asset: Asset) => void;
}) {
  const panelRef = useRef<HTMLElement>(null);

  // A generated image opened from the Image Studio history strip (or the asset library) is a
  // modal surface, but on its own the markup was not a dialog for a keyboard user: activation
  // left focus on the history item behind the panel, so the dialog was never announced; Escape
  // did nothing; and the first Tab walked out of the modal into the page behind it. Closing
  // unmounted the panel while focus still pointed inside it, dropping the reader to the top of
  // the document with no announcement of where it had gone.
  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    const active = document.activeElement;
    const opener = active && active !== document.body ? (active as HTMLElement) : null;
    // `tabIndex={-1}` lets the panel take focus without joining the tab order, and the focused
    // panel is named by aria-labelledby, so opening announces role "dialog" with the asset name.
    panel.focus();
    return () => {
      // Every close path unmounts the panel (Escape, the close control, backdrop, edit handoff),
      // so restoring here puts the reader back on the history item it activated.
      if (opener && document.contains(opener)) opener.focus();
    };
  }, []);

  function handleKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.key === "Escape") {
      // The dialog owns Escape while it is mounted; preventDefault keeps a parent handler from
      // also acting on the dismissal.
      event.preventDefault();
      onClose();
      return;
    }
    if (event.key !== "Tab") return;
    const panel = panelRef.current;
    if (!panel) return;
    const focusable = collectFocusable(panel);
    if (focusable.length === 0) {
      event.preventDefault();
      panel.focus();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement as HTMLElement | null;
    if (event.shiftKey) {
      // Forward focus from the panel itself is already the browser default (the panel wraps its
      // controls); backwards focus from it would leave the dialog, so it is wrapped here.
      if (active === first || active === panel) {
        event.preventDefault();
        last.focus();
      }
    } else if (active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  const date = new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "long", timeStyle: "short" }).format(new Date(asset.createdAt));
  const mediaUrl = assetFileUrl(asset.id, true);
  const isImage = asset.canPreview && asset.assetType === "image";
  const isVideo = asset.canPreview && asset.assetType === "video";
  const isAudio = asset.canPreview && (asset.assetType === "audio" || asset.assetType === "music");
  const isDocumentLike = asset.assetType === "document" || asset.assetType === "presentation" || asset.assetType === "research" || asset.assetType === "social";
  return <div className="modal-backdrop asset-detail-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) onClose(); }}>
    <section ref={panelRef} className="asset-detail-panel" role="dialog" aria-modal="true" aria-labelledby="asset-detail-title" tabIndex={-1} onKeyDown={handleKeyDown}>
      <div className="asset-detail-header"><div className="asset-detail-heading"><span className={`asset-detail-icon is-${asset.assetType}`}><AssetTypeIcon type={asset.assetType} size={25} /></span><div><p className="section-eyebrow">{labels.detailEyebrow}</p><h2 id="asset-detail-title">{asset.name}</h2></div></div><button type="button" className="modal-close" onClick={onClose} aria-label={labels.close}><X size={18} /></button></div>
      <div className="asset-detail-content">
        <div className={`asset-detail-preview is-${asset.assetType}`}>
          {isImage && <img src={mediaUrl} alt={asset.name} />}
          {isVideo && <video src={mediaUrl} controls playsInline preload="metadata" aria-label={asset.name} />}
          {isAudio && <div className="asset-detail-audio"><Headphones size={34} /><span>{asset.assetType === "music" ? "♪" : "◖◗"}</span><audio src={mediaUrl} controls preload="metadata" aria-label={asset.name} /></div>}
          {isDocumentLike && detailDocumentPreview(asset, labels)}
          {!isImage && !isVideo && !isAudio && !isDocumentLike && <div className="asset-detail-structured"><Layers3 size={33} /><strong>{labels.structuredTitle}</strong><p>{labels.structuredDescription}</p></div>}
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
