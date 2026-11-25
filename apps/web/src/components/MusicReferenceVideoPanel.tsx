"use client";

import { useId } from "react";
import { FileVideo, Info, LoaderCircle, XCircle } from "lucide-react";
import {
  describeReferenceVideoRejection,
  type ReferenceVideoCandidate,
  type ReferenceVideoState,
} from "@/lib/musicReferenceVideoState";

type ReferenceVideoPanelProps = {
  state: ReferenceVideoState;
  locale: string;
  t: (key: string) => string;
  /** Invoked with the picked file; the caller runs the validation contract. */
  onSelectFile: (file: { name: string; size: number; type: string }) => void;
  onClear: () => void;
};

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "";
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Presentational panel for MP4/MOV reference video selection in Music
 * Studio. It never touches storage or providers: every terminal state is
 * derived from the validation contract, so unsupported or unverified video
 * references are refused with an explicit reason instead of a silent
 * generic failure.
 */
export function ReferenceVideoPanel({ state, locale, t, onSelectFile, onClear }: ReferenceVideoPanelProps) {
  const titleId = useId();
  const statusId = useId();
  const candidate: ReferenceVideoCandidate | null = state.candidate;
  const fallback = describeReferenceVideoRejection(state.rejectedReasonKey, locale);

  return <section className="music-reference-video-panel" aria-labelledby={titleId}>
    <div className="music-reference-video-heading">
      <div><p className="music-overline">{t("music.referenceVideo.eyebrow")}</p><h3 id={titleId}>{t("music.referenceVideo.title")}</h3></div>
      <Info size={18} aria-hidden="true" />
    </div>
    <p className="music-reference-video-description">{t("music.referenceVideo.description")}</p>
    <div className="music-reference-video-picker">
      <button type="button" className="music-reference-video-select" onClick={() => onSelectFile({ name: "", size: 0, type: "" })}>{t("music.referenceVideo.chooseFile")}</button>
      <p className="music-reference-video-muted" id={statusId}>{t("music.referenceVideo.accept")}</p>
    </div>
    {state.loading ? <p className="music-reference-video-loading"><LoaderCircle size={14} className="music-spin" aria-hidden="true" /> {t("music.referenceVideo.loading")}</p> : candidate ? <div className="music-reference-video-selected">
      <div className="music-reference-video-file">
        <FileVideo size={18} aria-hidden="true" />
        <div><strong>{candidate.displayName}</strong><span>{candidate.sizeBytes ? formatSize(candidate.sizeBytes) : ""}</span></div>
      </div>
      <button type="button" className="music-reference-video-clear" onClick={onClear}>{t("music.referenceVideo.clear")}</button>
    </div> : state.rejectedReasonKey ? <p className="music-reference-video-error" role="alert"><XCircle size={14} aria-hidden="true" /> {fallback || t(state.rejectedReasonKey)}</p> : <p className="music-reference-video-empty">{t("music.referenceVideo.empty")}</p>}
    <p className="music-reference-video-note">{t("music.referenceVideo.note")}</p>
  </section>;
}
