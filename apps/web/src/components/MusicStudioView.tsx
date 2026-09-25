"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, AudioLines, CheckCircle2, Clock3, Download, FolderOpen, Headphones, LoaderCircle, Music2, Pause, Play, RefreshCw, Sparkles, WandSparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type MusicGenerationInput, type Project } from "@/lib/api";
import { canCancelMusicJob, isMusicProviderUnavailable, parseMusicJobResult } from "@/lib/musicStudioState";

const genres = ["auto", "ambient", "cinematic", "classical", "electronic", "folk", "hip_hop", "jazz", "lofi", "pop", "rock", "world", "other"] as const;
const moods = ["calm", "energetic", "uplifting", "melancholic", "inspiring", "dramatic", "playful", "romantic", "focused", "other"] as const;
const durations = [15, 30, 60, 120, 180, 300, 600] as const;
const vocalPreferences = ["instrumental", "vocal", "either"] as const;
const languages = ["auto", "en", "ar", "ku"] as const;

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

function formatAssetDate(value: string, locale: string) {
  try {
    return new Intl.DateTimeFormat(locale, { month: "short", day: "numeric" }).format(new Date(value));
  } catch {
    return "";
  }
}

function RecentMusicLibrary({ assets, loading, error, locale, t }: { assets: Asset[]; loading: boolean; error: string; locale: string; t: (key: string, values?: Record<string, string>) => string }) {
  return <section className="music-library-panel" aria-labelledby="music-library-title">
    <div className="music-section-heading">
      <div><p className="music-overline">{t("music.libraryEyebrow")}</p><h2 id="music-library-title">{t("music.libraryTitle")}</h2><p>{t("music.librarySubtitle")}</p></div>
      <Link className="music-text-link" href="/assets?type=music">{t("music.openAssets")} <span aria-hidden="true">↗</span></Link>
    </div>
    {loading ? <div className="music-library-loading"><LoaderCircle size={18} className="music-spin" /> <span>{t("music.libraryLoading")}</span></div> : error ? <div className="music-library-empty is-error"><AlertCircle size={20} /><div><strong>{t("music.libraryLoadError")}</strong><p>{error}</p></div></div> : assets.length === 0 ? <div className="music-library-empty"><Music2 size={24} /><div><strong>{t("music.libraryEmpty")}</strong><p>{t("music.libraryEmptyDescription")}</p></div></div> : <div className="music-library-list">{assets.map((asset) => <article className="music-library-row" key={asset.id}>
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

export function MusicStudioView() {
  const { workspace } = useAuth();
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
    } catch (caught) {
      setRecentMusic([]);
      setLibraryError(caught instanceof Error ? caught.message : t("music.libraryLoadError"));
    } finally {
      setLoadingLibrary(false);
    }
  }, [t, workspace]);

  // Loading remote workspace data is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); void loadRecentMusic(); }, [loadProjects, loadRecentMusic]);

  useEffect(() => {
    if (!current || current.status === "Succeeded" || current.status === "Failed" || current.status === "Cancelled") return;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(current.id);
        if (active) setCurrent(next);
      } catch (caught) {
        if (active) setError(caught instanceof Error ? caught.message : t("music.pollError"));
      }
    }, 650);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, t]);

  useEffect(() => {
    if (current?.status !== "Succeeded") return;
    const timer = window.setTimeout(() => void loadRecentMusic(), 0);
    return () => window.clearTimeout(timer);
  }, [current?.status, loadRecentMusic]);

  async function generate(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || description.trim().length < 3 || purpose.trim().length < 3) {
      setError(t("music.required"));
      return;
    }
    setWorking(true);
    setError("");
    const input: MusicGenerationInput = {
      workspaceId: workspace.id,
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
  }

  const result = useMemo(() => parseMusicJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";
  const failureCopy = current?.status === "Cancelled" ? { title: t("music.cancelledTitle"), text: t("music.cancelledText") } : isMusicProviderUnavailable(current) ? { title: t("music.providerUnavailableTitle"), text: t("music.providerUnavailableText") } : { title: t("music.failureTitle"), text: t("music.failureText") };

  return <div className="music-studio-page">
    <section className="music-studio-hero">
      <div className="music-studio-hero-copy"><div className="music-live-mark"><span /> {t("music.providerStatus")}</div><p className="music-overline">{t("music.eyebrow")}</p><h1>{t("music.title")}</h1><p className="music-studio-subtitle">{t("music.subtitle")}</p></div>
      <MusicArtwork large />
    </section>

    <div className="music-studio-notice"><WandSparkles size={17} /><p><strong>{t("music.providerUnavailableTitle")}</strong> {t("music.providerUnavailableText")}</p></div>

    {!current ? <form className="music-studio-workspace" onSubmit={(event) => void generate(event)}>
      <section className="music-create-panel">
        <div className="music-panel-heading"><span className="music-step-number">01</span><div><p className="music-overline">{t("music.createTitle")}</p><h2>{t("music.createSubtitle")}</h2></div></div>
        <label className="music-primary-field"><span>{t("music.descriptionLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("music.descriptionPlaceholder")} aria-label={t("music.descriptionLabel")} required /><small>{description.length}/4000</small></label>
        <label className="music-primary-field"><span>{t("music.purpose")}</span><textarea className="is-short" value={purpose} onChange={(event) => setPurpose(event.target.value)} maxLength={1000} placeholder={t("music.purposePlaceholder")} aria-label={t("music.purpose")} required /><small>{purpose.length}/1000</small></label>
        <div className="music-choice-grid">
          <label className="music-choice-field"><span>{t("music.genre")}</span><select value={genre} onChange={(event) => setGenre(event.target.value)}>{genres.map((value) => <option key={value} value={value}>{t(`music.genre.${value}`)}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.mood")}</span><select value={mood} onChange={(event) => setMood(event.target.value)}>{moods.map((value) => <option key={value} value={value}>{t(`music.mood.${value}`)}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.duration")}</span><select value={durationSeconds} onChange={(event) => setDurationSeconds(Number(event.target.value))}>{durations.map((value) => <option key={value} value={value}>{t("music.durationValue", { seconds: String(value) })}</option>)}</select></label>
          <label className="music-choice-field"><span>{t("music.vocalPreference")}</span><select value={vocalPreference} onChange={(event) => setVocalPreference(event.target.value)}>{vocalPreferences.map((value) => <option key={value} value={value}>{t(`music.vocal.${value}`)}</option>)}</select></label>
        </div>
        <details className="music-optional-controls"><summary>{t("music.moreOptions")}</summary><div className="music-optional-grid"><label className="music-choice-field"><span>{t("music.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value)}>{languages.map((value) => <option key={value} value={value}>{t(`music.language.${value}`)}</option>)}</select></label><label className="music-choice-field"><span>{t("music.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("music.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><label className="music-choice-field"><span>{t("music.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("music.titlePlaceholder")} /></label><label className="music-primary-field music-field-wide"><span>{t("music.additionalInstructions")}</span><textarea className="is-short" value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={2000} placeholder={t("music.additionalInstructionsPlaceholder")} /></label></div></details>
        {error && <div className="music-inline-error"><XCircle size={15} /> {error}</div>}
        <button className="music-generate-button" type="submit" disabled={working || description.trim().length < 3 || purpose.trim().length < 3}><Sparkles size={17} /> {working ? t("music.working") : t("music.generate")} <span aria-hidden="true">↗</span></button>
      </section>
      <aside className="music-side-panel"><div className="music-side-icon"><Headphones size={20} /></div><p className="music-overline">{t("music.guidanceTitle")}</p><h2>{t("music.guidanceText")}</h2><ol><li>{t("music.guidanceOne")}</li><li>{t("music.guidanceTwo")}</li><li>{t("music.guidanceThree")}</li></ol><div className="music-side-footer"><Clock3 size={14} /> {t("music.safetyNote")}</div></aside>
    </form> : isSuccess && result?.assetId ? <section className="music-result-panel" aria-live="polite">
      <div className="music-result-heading"><div><p className="music-overline">{t("music.resultEyebrow")}</p><h2>{t("music.resultTitle")}</h2></div><span className="music-success-badge"><CheckCircle2 size={15} /> {t("music.savedToAssets")}</span></div>
      <div className="music-result-hero"><MusicArtwork large /><div className="music-result-copy"><h3>{result.title ?? current.title ?? t("music.generatedTitle")}</h3><p>{result.format?.toUpperCase() ?? "AUDIO"}{result.durationSeconds ? ` · ${t("music.durationValue", { seconds: String(result.durationSeconds) })}` : ""}</p><AudioPlayer assetId={result.assetId} title={result.title ?? t("music.generatedTitle")} durationSeconds={result.durationSeconds} /></div></div>
      <div className="music-result-actions"><a className="music-action-button is-primary" href={api.assetFileUrl(result.assetId)}><Download size={15} /> {t("music.download")}</a>{current.projectId && <Link className="music-action-button" href={`/projects/${current.projectId}`}><FolderOpen size={15} /> {t("music.openProject")}</Link>}<Link className="music-action-button" href={`/assets?search=${encodeURIComponent(result.title ?? t("music.generatedTitle"))}`}><Music2 size={15} /> {t("music.openAssets")}</Link><button className="music-action-button is-quiet" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("music.createAnother")}</button></div>
    </section> : isFailure ? <section className="music-state-panel is-failure" aria-live="polite"><div className="music-state-icon"><AlertCircle size={24} /></div><p className="music-overline">{isMusicProviderUnavailable(current) ? t("music.providerStatus") : t("music.progressEyebrow")}</p><h2>{failureCopy.title}</h2><p>{failureCopy.text}</p><button className="music-action-button is-primary" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("music.retry")}</button></section> : <section className="music-state-panel" aria-live="polite"><div className="music-state-icon is-active"><LoaderCircle size={24} className="music-spin" /></div><p className="music-overline">{t("music.progressEyebrow")}</p><h2>{t(`jobs.status${current.status}`)}</h2><p>{t("music.progressText")}</p><div className="music-progress-meta"><span>{t("jobs.progress")}</span><strong>{current.progressPercent}%</strong></div><div className="music-progress-track"><span style={{ width: `${current.progressPercent}%` }} /></div>{canCancelMusicJob(current) && <button className="music-action-button" type="button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("music.cancel")}</button>}</section>}

    <RecentMusicLibrary assets={recentMusic} loading={loadingLibrary} error={libraryError} locale={locale} t={t} />
    <p className="music-studio-footnote">{t("music.safetyNote")}</p>
  </div>;
}
