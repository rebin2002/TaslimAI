"use client";
import { localeDirection, localeTag, type Locale } from "@/lib/i18n";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, AudioLines, CheckCircle2, Clock3, Download, FolderOpen, Headphones, LoaderCircle, Music2, Pause, Play, RefreshCw, Sparkles, WandSparkles, XCircle } from "lucide-react";
import { Info } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { AccessibleProgressBar } from "@/components/AccessibleProgressBar";
import { MediaJobArtifactDownload } from "@/components/MediaJobArtifactDownload";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type MusicGenerationInput, type Project } from "@/lib/api";
import { findMediaJobArtifact, mediaJobArtifactDownloadState } from "@/lib/mediaJobArtifactState";
import { canCancelMusicJob, isMusicProviderUnavailable, parseMusicJobResult } from "@/lib/musicStudioState";
import {
  decideMusicStudioSubmit,
  musicStudioPreflightCopy,
  type MusicStudioBriefDraft,
  type MusicStudioSubmitDecision,
} from "@/lib/musicStudioPreflight";
import {
  REFERENCE_AUDIO_ACCEPT,
  clearReferenceAudioSelection,
  formatReferenceAudioSize,
  localCandidateFromFile,
  referenceAudioPreviewUrl,
  revokeReferenceAudioObjectUrl,
  selectReferenceAudio,
  selectUnclaimedReferenceAudioCandidates,
  type ReferenceAudioCandidate,
  type ReferenceAudioState,
} from "@/lib/musicReferenceAudioState";

const genres = ["auto", "ambient", "cinematic", "classical", "electronic", "folk", "hip_hop", "jazz", "lofi", "pop", "rock", "world", "other"] as const;
const moods = ["calm", "energetic", "uplifting", "melancholic", "inspiring", "dramatic", "playful", "romantic", "focused", "other"] as const;
const durations = [15, 30, 60, 120, 180, 300, 600] as const;
const vocalPreferences = ["instrumental", "vocal", "either"] as const;
const languages = ["auto", "en", "ar", "ku"] as const;

// The in-flight status-read cadence. The first read keeps the studio's original
// 650ms wait; a failed read backs off linearly to a ceiling, so one transient
// 5xx can neither silence the studio nor hammer the API.
export const musicPollIntervalMs = 650;
export const musicPollMaxDelayMs = 8_000;

/** The wait before the next in-flight status read, given the failures before it. */
export function nextMusicPollDelay(retryAttempt = 0): number {
  return Math.min(musicPollIntervalMs * Math.max(1, retryAttempt + 1), musicPollMaxDelayMs);
}

/**
 * The in-flight status poll failed. The backoff keeps re-reading on its own, but
 * the member has to be told: this asserts over the polite progress region and
 * carries only the localized copy the studio already owns, never the provider's
 * own message, which is English and server-facing (`ApiError.message` is
 * "Something went wrong." for any unlabeled failure, src/lib/api.ts).
 *
 * It is fed `pollError`, the status-read slot, and nothing else. A refused
 * cancel is a claim about a different request, so it renders through
 * `MusicStudioActionNoticeView` with its own copy rather than being relabelled
 * as a polling failure.
 */
export function MusicStudioPollingNoticeView({ message }: { message: string }) {
  return (
    <div className="music-inline-error" role="alert" aria-live="assertive">
      <XCircle size={15} aria-hidden="true" />
      <span>{message}</span>
    </div>
  );
}

/**
 * A member-initiated action failed while the job was still in flight - today a
 * refused cancel. Rendered identically, because both are one localized line
 * above the action row, but a separate component fed by a separate slot so the
 * two claims cannot be conflated by state or withdrawn by a later status read.
 */
export function MusicStudioActionNoticeView({ message }: { message: string }) {
  return (
    <div className="music-inline-error" role="alert" aria-live="assertive">
      <XCircle size={15} aria-hidden="true" />
      <span>{message}</span>
    </div>
  );
}



type AudioPlayerProps = {
  assetId: string;
  title: string;
  durationSeconds?: number | null;
  compact?: boolean;
};

function formatTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = Math.floor(seconds % 60).toString().padStart(2, "0");
  return `${minutes}:${remainingSeconds}`;
}

function AudioPlayer({ assetId, title, durationSeconds = null, compact = false }: AudioPlayerProps) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const [playing, setPlaying] = useState(false);
  const [position, setPosition] = useState(0);
  const [duration, setDuration] = useState(durationSeconds ?? 0);

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    const updatePosition = () => setPosition(audio.currentTime);
    const updateDuration = () => setDuration(Number.isFinite(audio.duration) ? audio.duration : durationSeconds ?? 0);
    const stopPlaying = () => { setPlaying(false); setPosition(0); };
    audio.addEventListener("timeupdate", updatePosition);
    audio.addEventListener("loadedmetadata", updateDuration);
    audio.addEventListener("ended", stopPlaying);
    return () => {
      audio.removeEventListener("timeupdate", updatePosition);
      audio.removeEventListener("loadedmetadata", updateDuration);
      audio.removeEventListener("ended", stopPlaying);
    };
  }, [durationSeconds]);

  async function togglePlayback() {
    const audio = audioRef.current;
    if (!audio) return;
    if (audio.paused) {
      try {
        await audio.play();
        setPlaying(true);
      } catch {
        setPlaying(false);
      }
    } else {
      audio.pause();
      setPlaying(false);
    }
  }

  function seek(event: React.ChangeEvent<HTMLInputElement>) {
    const nextPosition = Number(event.target.value);
    setPosition(nextPosition);
    if (audioRef.current) audioRef.current.currentTime = nextPosition;
  }

  const max = duration > 0 ? duration : 1;
  return <div className={`music-player ${compact ? "is-compact" : ""}`}>
    <audio ref={audioRef} preload="metadata" crossOrigin="use-credentials" src={api.assetFileUrl(assetId, true)} aria-label={title} />
    <button type="button" className="music-play-button" onClick={() => void togglePlayback()} aria-label={playing ? "Pause" : "Play"}>
      {playing ? <Pause size={compact ? 14 : 18} fill="currentColor" /> : <Play size={compact ? 14 : 18} fill="currentColor" />}
    </button>
    <div className="music-player-track">
      <div className="music-player-times"><span>{formatTime(position)}</span><span>{formatTime(duration)}</span></div>
      <input className="music-timeline" type="range" min="0" max={max} step="0.1" value={Math.min(position, max)} onChange={seek} aria-label={`${title} timeline`} />
    </div>
  </div>;
}

function MusicArtwork({ large = false }: { large?: boolean }) {
  return <div className={`music-artwork ${large ? "is-large" : ""}`} aria-hidden="true">
    <div className="music-artwork-orbit" />
    <AudioLines size={large ? 30 : 20} />
    <div className="music-wave-bars"><i /><i /><i /><i /><i /><i /><i /></div>
  </div>;
}

export function musicLyricsLanguageTag(language: string, locale: Locale): string {
  // "auto" leaves the sung language to the provider, so the lyrics field follows
  // the interface locale, and Sorani resolves to its canonical ckb tag.
  return localeTag(language === "auto" ? locale : language);
}

export function musicLyricsTextDirection(language: string, locale: Locale): "ltr" | "rtl" {
  // Kurdish Sorani and Arabic lyrics are written in the Arabic script, so their
  // caret, bidi reordering and trailing punctuation must not inherit an English
  // shell's left-to-right base direction.
  const effective = language === "auto" ? locale : language;
  return localeDirection(effective as Locale);
}

function formatAssetDate(value: string, locale: string) {
  try {
    return new Intl.DateTimeFormat(localeTag(locale), { month: "short", day: "numeric" }).format(new Date(value));
  } catch {
    return "";
  }
}

// Browser-only helper kept separate from the pure state module so its tests
// stay deterministic without DOM globals.
function referenceAudioObjectUrlFrom(file: Blob): string | null {
  if (typeof URL === "undefined" || typeof URL.createObjectURL !== "function") return null;
  return URL.createObjectURL(file);
}

type ReferenceAudioPanelProps = {
  state: ReferenceAudioState;
  candidates: ReferenceAudioCandidate[];
  loading: boolean;
  t: (key: string, values?: Record<string, string>) => string;
  onFileSelected: (event: React.ChangeEvent<HTMLInputElement>) => void;
  onStoredPicked: (candidate: ReferenceAudioCandidate) => void;
  onClear: () => void;
};

// Selection and preview only: no upload, no job, and no provider call leaves
// the browser from this panel.
export function ReferenceAudioPanel({ state, candidates, loading, t, onFileSelected, onStoredPicked, onClear }: ReferenceAudioPanelProps) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const chooseButtonRef = useRef<HTMLButtonElement>(null);
  const selected = state.candidate;
  const previewUrl = selected ? (selected.sourceKind === "local" ? state.previewObjectUrl : referenceAudioPreviewUrl(selected, api.assetFileUrl)) : null;
  const storedId = selected?.sourceKind === "stored_asset" ? selected.assetId ?? "" : "";
  function handleClear() {
    onClear();
    // The clear button unmounts with the selection block. The chooser button
    // survives the state update, so focus moves there instead of dropping to
    // <body> and leaving the keyboard user at the top of the page.
    chooseButtonRef.current?.focus();
  }
  return <section className="music-reference-panel" aria-labelledby="music-reference-title">
    <div className="music-reference-heading"><div><p className="music-overline">{t("music.reference.eyebrow")}</p><h3 id="music-reference-title">{t("music.reference.title")}</h3></div><Info size={18} aria-hidden="true" /></div>
    <p className="music-reference-description">{t("music.reference.description")}</p>
    <div className="music-reference-picker">
      <input ref={fileInputRef} className="music-reference-input" type="file" accept={REFERENCE_AUDIO_ACCEPT} onChange={onFileSelected} aria-label={t("music.reference.uploadLabel")} />
      <button type="button" ref={chooseButtonRef} className="music-action-button" onClick={() => fileInputRef.current?.click()}>{t("music.reference.chooseFile")}</button>
      <span>{t("music.reference.fileHint")}</span>
    </div>
    {/* One persistent status paragraph: the private library query resolves
        while this panel is already on screen, so the loading -> empty swap
        lands in the same live region and is announced instead of silently
        swapping two different paragraphs. */}
    <p className="music-reference-muted music-reference-status" role="status">{loading ? t("music.reference.loading") : candidates.length === 0 ? t("music.reference.empty") : ""}</p>
    {!loading && candidates.length > 0 ? <label className="music-choice-field music-reference-select"><span>{t("music.reference.savedLabel")}</span><select value={storedId} onChange={(event) => {
      const picked = event.target.value ? candidates.find((candidate) => candidate.assetId === event.target.value) : null;
      if (picked) onStoredPicked(picked);
    }}><option value="">{t("music.reference.savedPlaceholder")}</option>{candidates.map((candidate) => <option key={candidate.id} value={candidate.assetId ?? ""}>{candidate.displayName}</option>)}</select></label> : null}
    {state.rejectedReasonKey ? <p className="music-reference-error" role="alert">{t(state.rejectedReasonKey)}</p> : null}
    <div className="music-reference-live" aria-live="polite">
      {selected ? <div className="music-reference-selected">
        <div className="music-reference-file"><MusicArtwork /><div><strong>{selected.displayName}</strong><span>{formatReferenceAudioSize(selected.sizeBytes) ?? t("music.reference.metadataUnavailable")}</span></div><button type="button" className="music-reference-clear" onClick={handleClear}>{t("music.reference.clear")}</button></div>
        {previewUrl ? <audio className="music-reference-audio" controls preload="metadata" src={previewUrl} aria-label={`${t("music.reference.previewLabel")}: ${selected.displayName}`} /> : <p className="music-reference-muted">{t("music.reference.previewUnavailable")}</p>}
        <p className="music-reference-note">{t("music.reference.note")}</p>
      </div> : null}
    </div>
  </section>;
}

type MusicGenerationActionProps = Readonly<{
  decision: MusicStudioSubmitDecision;
  working: boolean;
  noticeId: string;
  t: (key: string) => string;
}>;

// The studio's only route to a generation request, rendered from the preflight
// decision so the copy that pauses creation and the state of the action cannot
// disagree. While the pause is what blocks the brief the button says so: a dead
// control with no announced reason is a dead end for keyboard and screen-reader
// users, and the notice is the only place that reason lives.
export function MusicGenerationAction({ decision, working, noticeId, t }: MusicGenerationActionProps) {
  return <button
    className={decision.reasonKey ? "music-generate-button is-paused" : "music-generate-button"}
    type="submit"
    disabled={!decision.permitted}
    aria-disabled={!decision.permitted || undefined}
    aria-describedby={decision.reasonKey ? noticeId : undefined}
    title={decision.reasonKey ? t(decision.reasonKey) : undefined}
  ><Sparkles size={17} /> {working ? t("music.working") : t("music.generate")} <span aria-hidden="true">↗</span></button>;
}

function RecentMusicLibrary({ assets, loading, error, locale, t }: { assets: Asset[]; loading: boolean; error: string; locale: string; t: (key: string, values?: Record<string, string>) => string }) {
  // The error card keeps this component's two-line shape, but the bold line is
  // the panel's own label and `error` is the message slot, so the failure is
  // stated once and always in the member's language. Before this change the
  // slot carried `ApiError.message` - English server prose, src/lib/api.ts -
  // under the localized `music.libraryLoadError` heading, so a Kurdish member
  // read one claim in ku and the same claim again in English. The apostrophe
  // free wording keeps this lane clear of react/no-unescaped-entities.
  return <section className="music-library-panel" aria-labelledby="music-library-title">
    <div className="music-section-heading">
      <div><p className="music-overline">{t("music.libraryEyebrow")}</p><h2 id="music-library-title">{t("music.libraryTitle")}</h2><p>{t("music.librarySubtitle")}</p></div>
      <Link className="music-text-link" href="/assets?type=music">{t("music.openAssets")} <span aria-hidden="true">↗</span></Link>
    </div>
    {loading ? <div className="music-library-loading"><LoaderCircle size={18} className="music-spin" /> <span>{t("music.libraryLoading")}</span></div> : error ? <div className="music-library-empty is-error"><AlertCircle size={20} /><div><strong>{t("music.libraryTitle")}</strong><p>{error}</p></div></div> : assets.length === 0 ? <div className="music-library-empty"><Music2 size={24} /><div><strong>{t("music.libraryEmpty")}</strong><p>{t("music.libraryEmptyDescription")}</p></div></div> : <div className="music-library-list">{assets.map((asset) => <article className="music-library-row" key={asset.id}>
      <MusicArtwork />
      <div className="music-library-main">
        <div className="music-library-title"><div><h3>{asset.name}</h3><p>{asset.projectName ?? t("music.workspaceTrack")}</p></div><time dateTime={asset.createdAt}>{formatAssetDate(asset.createdAt, locale)}</time></div>
        {asset.hasFile && asset.canPreview ? <AudioPlayer assetId={asset.id} title={asset.name} compact /> : <span className="music-file-unavailable">{t("assets.fileUnavailable")}</span>}
      </div>
      <div className="music-library-actions">
        {asset.hasFile && <a className="music-icon-action" href={api.assetFileUrl(asset.id)} aria-label={`${t("music.download")} ${asset.name}`}><Download size={16} /></a>}
        {asset.projectId && <Link className="music-icon-action" href={`/projects/${asset.projectId}`} aria-label={`${t("music.openProject")} ${asset.projectName ?? ""}`}><FolderOpen size={16} /></Link>}
        <Link className="music-icon-action" href={`/assets?search=${encodeURIComponent(asset.name)}`} aria-label={`${t("music.openAssets")} ${asset.name}`}><span aria-hidden="true">↗</span></Link>
      </div>
    </article>)}</div>}
  </section>;
}

// A workspace change must unmount the previous private library/preview state.
// Keying the whole studio also prevents in-flight results from the prior
// workspace from appearing in the next workspace and releases local Blob URLs.
export function MusicStudioView() {
  const { workspace } = useAuth();
  return <WorkspaceMusicStudio key={workspace?.id ?? "no-workspace"} workspace={workspace} />;
}

function WorkspaceMusicStudio({ workspace }: { workspace: ReturnType<typeof useAuth>["workspace"] }) {
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [recentMusic, setRecentMusic] = useState<Asset[]>([]);
  const [description, setDescription] = useState("");
  const [purpose, setPurpose] = useState("");
  const [genre, setGenre] = useState("auto");
  const [mood, setMood] = useState("calm");
  const [durationSeconds, setDurationSeconds] = useState(60);
  const [vocalPreference, setVocalPreference] = useState("instrumental");
  const [language, setLanguage] = useState("auto");
  const [title, setTitle] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [working, setWorking] = useState(false);
  const [loadingProjects, setLoadingProjects] = useState(true);
  const [loadingLibrary, setLoadingLibrary] = useState(true);
  const [libraryError, setLibraryError] = useState("");
  const [error, setError] = useState("");
  // Counts consecutive failed status reads. It is both the backoff input and
  // the polling effect's dependency: without it a rejected read leaves every
  // dependency unchanged, the effect never re-runs and the studio stops
  // updating while still announcing an in-flight generation.
  const [pollRetry, setPollRetry] = useState(0);
  // The status-read failure is its own slot, separate from `error`. One string
  // cannot hold two claims: `error` belongs to member actions (submit, cancel)
  // and is replaced only by the next action, while `pollError` belongs to the
  // background read and is withdrawn by the next good read. Sharing the slot
  // made a refused cancel announce itself as a polling failure, and forced the
  // good-read cleanup to guess which claim it held by comparing translated copy
  // - a check that silently stops working wherever two locales happen to share
  // a string.
  const [pollError, setPollError] = useState("");
  const [referenceAudio, setReferenceAudio] = useState<ReferenceAudioState>({ candidate: null, previewObjectUrl: null, rejectedReasonKey: null });
  const [referenceAudioAssets, setReferenceAudioAssets] = useState<Asset[]>([]);
  const [loadingReferenceLibrary, setLoadingReferenceLibrary] = useState(true);
  // The File object behind a local candidate: object URLs need the real Blob,
  // while the pure state module stays testable without browser globals.
  const applyReferenceAudioResult = useCallback((result: { state: ReferenceAudioState }) => {
    // The preview URL effect revokes the prior URL exactly once when this
    // dependency changes (and also on unmount).
    setReferenceAudio(result.state);
  }, []);

  const loadProjects = useCallback(async () => {
    if (!workspace) return;
    setLoadingProjects(true);
    try {
      const [active, archived] = await Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived")]);
      setProjects([...active, ...archived]);
    } catch {
      setProjects([]);
    } finally {
      setLoadingProjects(false);
    }
  }, [workspace]);

  const loadRecentMusic = useCallback(async () => {
    if (!workspace) return;
    setLoadingLibrary(true);
    setLibraryError("");
    try {
      const result = await api.listAssets(workspace.id, { assetType: "music", status: "Active", sort: "recent", page: 1, pageSize: 5 });
      setRecentMusic(result.items);
    } catch {
      // Localized copy, never the rejection's own text, for the same reason as
      // the Voice compose form: `ApiError.message` is the server's English
      // string (`src/lib/api.ts`: `body?.error?.message ?? "Something went
      // wrong."`) and this slot renders inside the library panel, which a ku
      // or ar member reads in their own language. Listing recent music
      // involves no provider, so the raw text would carry no information the
      // member could act on. `error` stays the message slot and the bold line
      // keeps the panel's own label, so the card states the failure once.
      setRecentMusic([]);
      setLibraryError(t("music.libraryLoadError"));
    } finally {
      setLoadingLibrary(false);
    }
  }, [t, workspace]);

  // Unclaimed reference candidates are the caller's own private, project-free
  // audio assets. The asset query is workspace-membership scoped server-side;
  // no provider call is involved in listing or previewing them.
  const loadReferenceAudioCandidates = useCallback(async () => {
    if (!workspace) return;
    setLoadingReferenceLibrary(true);
    try {
      const result = await api.listAssets(workspace.id, { status: "Active", page: 1, pageSize: 100 });
      setReferenceAudioAssets(result.items);
    } catch {
      setReferenceAudioAssets([]);
    } finally {
      setLoadingReferenceLibrary(false);
    }
  }, [workspace]);

  // Loading remote workspace data is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); void loadRecentMusic(); void loadReferenceAudioCandidates(); }, [loadProjects, loadRecentMusic, loadReferenceAudioCandidates]);

  useEffect(() => {
    if (!current || current.status === "Succeeded" || current.status === "Failed" || current.status === "Cancelled") return;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(current.id);
        if (active) {
          setCurrent(next);
          setPollRetry(0);
          // A good read says nothing about whether an earlier cancel was
          // accepted, so it dismisses only its own notice.
          setPollError("");
        }
      } catch {
        if (active) {
          setPollRetry((attempt) => attempt + 1);
          // The read failed, not the generation: keep the last known status
          // visible and say only what the studio can claim, in the member's own
          // locale. The rejection's own text is deliberately not surfaced,
          // because ApiError carries the server's English message.
          setPollError(t("music.pollError"));
        }
      }
    }, nextMusicPollDelay(pollRetry));
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);

  useEffect(() => {
    if (current?.status !== "Succeeded") return;
    const timer = window.setTimeout(() => void loadRecentMusic(), 0);
    return () => window.clearTimeout(timer);
  }, [current?.status, loadRecentMusic]);

  // Release any local preview object URL when the studio unmounts.
  useEffect(() => () => { revokeReferenceAudioObjectUrl(referenceAudio.previewObjectUrl); }, [referenceAudio.previewObjectUrl]);

  function handleReferenceFilePicked(event: React.ChangeEvent<HTMLInputElement>) {
    const files = event.target.files;
    const picked = files && files.length > 0 ? files[0] : null;
    // Allow re-picking the same file later.
    event.target.value = "";
    if (!picked) return;
    const handle = { name: picked.name, size: picked.size, mimeType: picked.type ?? "" };
    applyReferenceAudioResult(selectReferenceAudio(referenceAudio, localCandidateFromFile(handle), (candidate) => (
      candidate.sourceKind === "local" ? referenceAudioObjectUrlFrom(picked) : null
    )));
  }

  function handleReferenceStoredPicked(candidate: ReferenceAudioCandidate) {
    applyReferenceAudioResult(selectReferenceAudio(referenceAudio, candidate, () => null));
  }

  function handleReferenceAudioClear() {
    applyReferenceAudioResult(clearReferenceAudioSelection(referenceAudio));
  }

  const workspaceId = workspace?.id ?? "";
  // One decision drives the disabled action, the reason announced on it and the
  // submit guard below, so the affordance can never again promise a request the
  // published preflight copy says will not run.
  const submitDraft: MusicStudioBriefDraft = {
    workspaceReady: workspaceId !== "",
    submitting: working,
    referenceSelected: referenceAudio.candidate !== null,
    description,
    purpose,
  };
  const submitDecision = decideMusicStudioSubmit(submitDraft);
  const preflightCopy = musicStudioPreflightCopy();

  async function generate(event: React.FormEvent) {
    event.preventDefault();
    // Paused creation outranks the brief itself: nothing leaves the browser, so
    // no private description is posted to a route that can only refuse it and no
    // request can reach a provider. The same guard covers implicit submission
    // from a text field, where the disabled button would not have helped.
    if (!submitDecision.permitted) {
      if (submitDecision.messageKey) setError(t(submitDecision.messageKey));
      return;
    }
    setWorking(true);
    setError("");
    // A fresh job starts on the base cadence: a backoff accumulated by the
    // previous job must not delay the first read of this one.
    setPollRetry(0);
    setPollError("");
    const input: MusicGenerationInput = {
      workspaceId,
      projectId: projectId || null,
      description: description.trim(),
      purpose: purpose.trim(),
      genre,
      mood,
      durationSeconds,
      vocalPreference,
      language,
      title: title.trim() || null,
      additionalInstructions: additionalInstructions.trim() || null,
    };
    try {
      setCurrent(await api.createMusicGenerationJob(input));
    } catch {
      setError(t("music.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancel() {
    if (!current) return;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(current.id);
      setCurrent(await api.getGenerationJob(current.id));
    } catch {
      setError(t("music.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
    setCurrent(null);
    setError("");
    setPollError("");
  }

  const result = useMemo(() => parseMusicJobResult(current), [current]);
  // The reload the succeeded job triggers puts this artifact's own row in the
  // loaded list, which is what tells the download control whether the stored
  // file is still there.
  const resultArtifact = useMemo(() => findMediaJobArtifact(recentMusic, result?.assetId), [recentMusic, result]);
  const resultArtifactState = mediaJobArtifactDownloadState(resultArtifact);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";
  const referenceCandidates = useMemo(() => selectUnclaimedReferenceAudioCandidates(referenceAudioAssets), [referenceAudioAssets]);
  const failureCopy = current?.status === "Cancelled" ? { title: t("music.cancelledTitle"), text: t("music.cancelledText") } : isMusicProviderUnavailable(current) ? { title: t("music.providerUnavailableTitle"), text: t("music.providerUnavailableText") } : { title: t("music.failureTitle"), text: t("music.failureText") };

  return <div className="music-studio-page">
    <section className="music-studio-hero">
      <div className="music-studio-hero-copy">{preflightCopy ? <div className="music-live-mark"><span /> {t(preflightCopy.eyebrowKey)}</div> : null}<p className="music-overline">{t("music.eyebrow")}</p><h1>{t("music.title")}</h1><p className="music-studio-subtitle">{t("music.subtitle")}</p></div>
      <MusicArtwork large />
    </section>

    {/* The preflight notice is the reason the primary action is inert, so it is
         referenced by the button rather than sitting next to it as decoration. */}
    {preflightCopy ? <div id="music-preflight-note" className="music-studio-notice" role="status"><WandSparkles size={17} aria-hidden="true" /><p><strong>{t(preflightCopy.titleKey)}</strong> {t(preflightCopy.textKey)}</p></div> : null}

    {!current ? <form className="music-studio-workspace" onSubmit={(event) => void generate(event)}>
      <section className="music-create-panel">
        <div className="music-panel-heading"><span className="music-step-number">01</span><div><p className="music-overline">{t("music.createTitle")}</p><h2>{t("music.createSubtitle")}</h2></div></div>
        <label className="music-primary-field"><span>{t("music.descriptionLabel")}</span><textarea dir={musicLyricsTextDirection(language, locale)} lang={musicLyricsLanguageTag(language, locale)} value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("music.descriptionPlaceholder")} aria-label={t("music.descriptionLabel")} required /><small>{description.length}/4000</small></label>
        <label className="music-primary-field"><span>{t("music.purpose")}</span><textarea className="is-short" dir={musicLyricsTextDirection(language, locale)} lang={musicLyricsLanguageTag(language, locale)} value={purpose} onChange={(event) => setPurpose(event.target.value)} maxLength={1000} placeholder={t("music.purposePlaceholder")} aria-label={t("music.purpose")} required /><small>{purpose.length}/1000</small></label>
        <div className="music-choice-grid">
          <label className="music-choice-field"><span>{t("music.genre")}</span><select value={genre} onChange={(event) => setGenre(event.target.value)}>{genres.map((value) => <option key={value} value={value}>{t(`music.genre.${value}`)}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.mood")}</span><select value={mood} onChange={(event) => setMood(event.target.value)}>{moods.map((value) => <option key={value} value={value}>{t(`music.mood.${value}`)}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.duration")}</span><select value={durationSeconds} onChange={(event) => setDurationSeconds(Number(event.target.value))}>{durations.map((value) => <option key={value} value={value}>{t("music.durationValue", { seconds: String(value) })}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.vocalPreference")}</span><select value={vocalPreference} onChange={(event) => setVocalPreference(event.target.value)}>{vocalPreferences.map((value) => <option key={value} value={value}>{t(`music.vocal.${value}`)}</option>)}</select></label>
        </div>
        <details className="music-optional-controls"><summary>{t("music.moreOptions")}</summary><div className="music-optional-grid"><label className="music-choice-field"><span>{t("music.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value)}>{languages.map((value) => <option key={value} value={value}>{t(`music.language.${value}`)}</option>)}</select></label><label className="music-choice-field"><span>{t("music.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("music.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><label className="music-choice-field"><span>{t("music.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("music.titlePlaceholder")} /></label><label className="music-primary-field music-field-wide"><span>{t("music.additionalInstructions")}</span><textarea className="is-short" value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={2000} placeholder={t("music.additionalInstructionsPlaceholder")} /></label></div></details>
        <ReferenceAudioPanel state={referenceAudio} candidates={referenceCandidates} loading={loadingReferenceLibrary} t={t} onFileSelected={handleReferenceFilePicked} onStoredPicked={handleReferenceStoredPicked} onClear={handleReferenceAudioClear} />
        {error && <div className="music-inline-error"><XCircle size={15} /> {error}</div>}
        <MusicGenerationAction decision={submitDecision} working={working} noticeId="music-preflight-note" t={t} />
      </section>
      <aside className="music-side-panel"><div className="music-side-icon"><Headphones size={20} /></div><p className="music-overline">{t("music.guidanceTitle")}</p><h2>{t("music.guidanceText")}</h2><ol><li>{t("music.guidanceOne")}</li><li>{t("music.guidanceTwo")}</li><li>{t("music.guidanceThree")}</li></ol><div className="music-side-footer"><Clock3 size={14} /> {t("music.safetyNote")}</div></aside>
    </form> : isSuccess && result?.assetId ? <section className="music-result-panel" aria-live="polite">
      <div className="music-result-heading"><div><p className="music-overline">{t("music.resultEyebrow")}</p><h2>{t("music.resultTitle")}</h2></div><span className="music-success-badge"><CheckCircle2 size={15} /> {t("music.savedToAssets")}</span></div>
      <div className="music-result-hero"><MusicArtwork large /><div className="music-result-copy"><h3>{result.title ?? current.title ?? t("music.generatedTitle")}</h3><p>{result.format?.toUpperCase() ?? "AUDIO"}{result.durationSeconds ? ` · ${t("music.durationValue", { seconds: String(result.durationSeconds) })}` : ""}</p><AudioPlayer assetId={result.assetId} title={result.title ?? t("music.generatedTitle")} durationSeconds={result.durationSeconds} /></div></div>
      <div className="music-result-actions"><MediaJobArtifactDownload href={api.assetFileUrl(result.assetId)} state={resultArtifactState} className="music-action-button is-primary" label={t("music.download")} unavailableLabel={t("assets.fileUnavailable")} />{current.projectId && <Link className="music-action-button" href={`/projects/${current.projectId}`}><FolderOpen size={15} /> {t("music.openProject")}</Link>}<Link className="music-action-button" href={`/assets?search=${encodeURIComponent(result.title ?? t("music.generatedTitle"))}`}><Music2 size={15} /> {t("music.openAssets")}</Link><button className="music-action-button is-quiet" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("music.createAnother")}</button></div>
    </section> : isFailure ? <section className="music-state-panel is-failure" aria-live="polite"><div className="music-state-icon"><AlertCircle size={24} /></div><p className="music-overline">{isMusicProviderUnavailable(current) ? t("music.providerStatus") : t("music.progressEyebrow")}</p><h2>{failureCopy.title}</h2><p>{failureCopy.text}</p><button className="music-action-button is-primary" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("music.retry")}</button></section> : <section className="music-state-panel" aria-live="polite"><div className="music-state-icon is-active"><LoaderCircle size={24} className="music-spin" aria-hidden="true" /></div><p className="music-overline">{t("music.progressEyebrow")}</p><h2>{t(`jobs.status${current.status}`)}</h2><p>{t("music.progressText")}</p><div className="music-progress-meta"><span>{t("jobs.progress")}</span><strong>{current.progressPercent}%</strong></div><AccessibleProgressBar className="music-progress-track" label={t("jobs.progress")} value={current.progressPercent} />{pollError && <MusicStudioPollingNoticeView message={pollError} />}{error && <MusicStudioActionNoticeView message={error} />}{canCancelMusicJob(current) && <button className="music-action-button" type="button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("music.cancel")}</button>}</section>}

    <RecentMusicLibrary assets={recentMusic} loading={loadingLibrary} error={libraryError} locale={locale} t={t} />
    <p className="music-studio-footnote">{t("music.safetyNote")}</p>
  </div>;
}
