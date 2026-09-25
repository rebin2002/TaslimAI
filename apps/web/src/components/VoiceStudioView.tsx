"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  AlertCircle,
  Check,
  CheckCircle2,
  Clock3,
  Download,
  FileAudio,
  Headphones,
  LoaderCircle,
  Mic2,
  Pause,
  Play,
  RefreshCw,
  Sparkles,
  Volume2,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type Project, type VoiceGenerationInput } from "@/lib/api";
import { canCancelVoiceJob, formatVoiceDuration, formatVoiceFileSize, isVoiceAsset, parseVoiceJobResult } from "@/lib/voiceStudioState";

const languages = ["en", "ar", "ku"] as const;
const voiceStyles = ["neutral", "warm", "professional", "storytelling"] as const;
const speakingStyles = ["conversational", "clear", "expressive", "calm"] as const;

type AudioPlayerProps = {
  src: string;
  label: string;
  durationMilliseconds?: number | null;
  compact?: boolean;
};

function VoiceAudioPlayer({ src, label, durationMilliseconds, compact = false }: AudioPlayerProps) {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(durationMilliseconds ? durationMilliseconds / 1000 : 0);

  const togglePlayback = async () => {
    const audio = audioRef.current;
    if (!audio) return;
    if (audio.paused) {
      await audio.play();
      setIsPlaying(true);
    } else {
      audio.pause();
      setIsPlaying(false);
    }
  };

  const seek = (event: React.ChangeEvent<HTMLInputElement>) => {
    const nextTime = Number(event.target.value);
    if (audioRef.current) audioRef.current.currentTime = nextTime;
    setCurrentTime(nextTime);
  };

  return (
    <div className={`voice-audio-player${compact ? " is-compact" : ""}`}>
      <audio
        ref={audioRef}
        preload="metadata"
        crossOrigin="use-credentials"
        src={src}
        onLoadedMetadata={(event) => setDuration(Number.isFinite(event.currentTarget.duration) ? event.currentTarget.duration : duration)}
        onTimeUpdate={(event) => setCurrentTime(event.currentTarget.currentTime)}
        onPlay={() => setIsPlaying(true)}
        onPause={() => setIsPlaying(false)}
        onEnded={() => { setIsPlaying(false); setCurrentTime(0); }}
      />
      <button className="voice-play-button" type="button" onClick={() => void togglePlayback()} aria-label={isPlaying ? `Pause ${label}` : `Play ${label}`}>
        {isPlaying ? <Pause size={compact ? 16 : 20} fill="currentColor" /> : <Play size={compact ? 16 : 20} fill="currentColor" />}
      </button>
      <div className="voice-audio-track">
        <div className="voice-audio-times"><span>{formatVoiceDuration(currentTime * 1000)}</span><span>{formatVoiceDuration(duration * 1000)}</span></div>
        <input
          className="voice-timeline"
          type="range"
          min="0"
          max={duration || 0}
          step="0.1"
          value={Math.min(currentTime, duration || 0)}
          onChange={seek}
          disabled={!duration}
          aria-label={`${label} progress`}
        />
      </div>
    </div>
  );
}

function formatCreatedAt(value: string, locale: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(date);
}

export function VoiceStudioView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [recentAssets, setRecentAssets] = useState<Asset[]>([]);
  const [text, setText] = useState("");
  const [language, setLanguage] = useState<VoiceGenerationInput["language"]>("en");
  const [voiceStyle, setVoiceStyle] = useState<string>("neutral");
  const [speakingStyle, setSpeakingStyle] = useState<string>("clear");
  const [instructions, setInstructions] = useState("");
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [working, setWorking] = useState(false);
  const [loadingProjects, setLoadingProjects] = useState(true);
  const [loadingRecent, setLoadingRecent] = useState(true);
  const [error, setError] = useState("");

  const loadWorkspaceData = useCallback(async () => {
    if (!workspace) return;
    setLoadingProjects(true);
    setLoadingRecent(true);
    const [projectResult, assetResult] = await Promise.allSettled([
      Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived")]),
      api.listAssets(workspace.id, { assetType: "audio", sort: "recent", pageSize: 12 }),
    ]);
    if (projectResult.status === "fulfilled") setProjects([...projectResult.value[0], ...projectResult.value[1]]);
    else setProjects([]);
    if (assetResult.status === "fulfilled") setRecentAssets(assetResult.value.items.filter(isVoiceAsset).slice(0, 6));
    else setRecentAssets([]);
    setLoadingProjects(false);
    setLoadingRecent(false);
  }, [workspace]);

  // This effect synchronizes the authenticated workspace with its remote project and asset lists.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadWorkspaceData(); }, [loadWorkspaceData]);

  useEffect(() => {
    if (!current || current.status === "Succeeded" || current.status === "Failed" || current.status === "Cancelled") return;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(current.id);
        if (active) setCurrent(next);
      } catch (caught) {
        if (active) setError(caught instanceof Error ? caught.message : t("voice.pollError"));
      }
    }, 650);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, t]);

  async function generate(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || text.trim().length < 1) {
      setError(t("voice.textRequired"));
      return;
    }
    setWorking(true);
    setError("");
    const input: VoiceGenerationInput = {
      workspaceId: workspace.id,
      projectId: projectId || null,
      text: text.trim(),
      language,
      voiceStyle,
      speakingStyle,
      instructions: instructions.trim() || null,
    };
    try {
      setCurrent(await api.createVoiceGenerationJob(input));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("voice.createError"));
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
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("voice.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
    setCurrent(null);
    setError("");
    window.setTimeout(() => document.getElementById("voice-script")?.focus(), 0);
  }

  const result = useMemo(() => parseVoiceJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";
  const unavailable = current?.errorCode === "VOICE_PROVIDER_UNAVAILABLE";
  const unsupportedLanguage = current?.errorCode === "VOICE_LANGUAGE_UNSUPPORTED";
  const cancelled = current?.errorCode === "VOICE_CANCELLED";
  const isGenerating = !!current && !isSuccess && !isFailure;

  return (
    <div className="voice-studio-page">
      <header className="voice-studio-header">
        <div className="voice-studio-heading">
          <div className="voice-studio-title-row"><span className="voice-studio-header-icon"><Volume2 size={20} /></span><p className="section-eyebrow">{t("voice.eyebrow")}</p></div>
          <h1>{t("voice.title")}</h1>
          <p>{t("voice.subtitle")}</p>
        </div>
        <div className="voice-private-badge"><Sparkles size={13} /> {t("voice.privateBadge")}</div>
      </header>

      <nav className="voice-workflow" aria-label={t("voice.workflowLabel")}>
        {["script", "choices", "generate", "listen"].map((step, index) => (
          <div className={`voice-workflow-step ${index === 0 && !current ? "is-active" : ""} ${index > 0 && !current ? "is-upcoming" : ""}`} key={step}>
            <span>{index + 1}</span><strong>{t(`voice.workflow.${step}`)}</strong>{index < 3 && <i aria-hidden="true" />}
          </div>
        ))}
      </nav>

      {isSuccess && result ? (
        <section className="voice-result-panel" aria-live="polite">
          <div className="voice-result-topline"><div><p className="section-eyebrow">{t("voice.resultEyebrow")}</p><h2>{t("voice.resultTitle")}</h2><p>{t("voice.resultSubtitle")}</p></div><span className="voice-success-badge"><CheckCircle2 size={15} /> {t("voice.savedToAssets")}</span></div>
            <div className="voice-result-card">
            <div className="voice-result-art"><FileAudio size={27} /><span>{result.format?.toUpperCase() ?? "AUDIO"}</span></div>
            <div className="voice-result-copy"><strong>{text.trim().slice(0, 72) || t("voice.untitledAudio")}</strong><span>{t(`voice.language.${result.language ?? language}`)} · {t(`voice.style.${result.voiceStyle ?? voiceStyle}`)} · {t(`voice.speaking.${result.speakingStyle ?? speakingStyle}`)}</span></div>
            <VoiceAudioPlayer src={api.assetFileUrl(result.assetId!, true)} label={t("voice.resultTitle")} durationMilliseconds={result.durationMilliseconds} />
          </div>
          <div className="voice-result-actions"><a className="voice-secondary-action" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("voice.download")}</a><Link className="voice-secondary-action" href={`/assets?search=${encodeURIComponent(text.slice(0, 60))}`}>{t("voice.openAssets")}</Link><button className="voice-primary-action" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("voice.createAnother")}</button></div>
        </section>
      ) : isGenerating ? (
        <section className="voice-generation-panel" aria-live="polite">
          <div className="voice-generating-icon"><LoaderCircle size={27} /></div><p className="section-eyebrow">{t("voice.progressEyebrow")}</p><h2>{t(`jobs.status${current?.status ?? "Queued"}`)}</h2><p>{t("voice.progressText")}</p>
          <div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{current?.progressPercent ?? 0}%</strong></div><div className="generation-progress-track"><span style={{ width: `${current?.progressPercent ?? 0}%` }} /></div>
          {canCancelVoiceJob(current) && <button className="voice-secondary-action voice-cancel-action" type="button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("voice.cancel")}</button>}
        </section>
      ) : (
        <form className="voice-studio-workspace" onSubmit={(event) => void generate(event)}>
          <section className="voice-editor-panel">
            <div className="voice-panel-heading"><div><p className="section-eyebrow">01 / {t("voice.workflow.script")}</p><h2>{t("voice.scriptTitle")}</h2><p>{t("voice.scriptSubtitle")}</p></div><span className="voice-panel-icon"><Mic2 size={18} /></span></div>
            <label className="voice-script-field" htmlFor="voice-script"><span>{t("voice.textLabel")}</span><textarea id="voice-script" dir={language === "en" ? "ltr" : "rtl"} value={text} onChange={(event) => setText(event.target.value)} maxLength={10000} placeholder={t("voice.textPlaceholder")} required /><small>{text.length.toLocaleString(locale)}/10,000</small></label>
            <div className="voice-editor-footer"><span><Headphones size={14} /> {t("voice.scriptHint")}</span><span>{t(`voice.language.${language}`)}</span></div>
          </section>

          <section className="voice-choices-panel">
            <div className="voice-panel-heading"><div><p className="section-eyebrow">02 / {t("voice.workflow.choices")}</p><h2>{t("voice.choicesTitle")}</h2><p>{t("voice.choicesSubtitle")}</p></div><span className="voice-panel-icon"><Sparkles size={18} /></span></div>
            <div className="voice-choice-group"><span className="voice-choice-label">{t("voice.language")}</span><div className="voice-choice-grid voice-language-choices" role="radiogroup" aria-label={t("voice.language")}>{languages.map((value) => <button key={value} type="button" role="radio" aria-checked={language === value} className={language === value ? "is-selected" : ""} onClick={() => setLanguage(value)}>{t(`voice.language.${value}`)}{language === value && <Check size={14} />}</button>)}</div></div>
            <div className="voice-choice-group"><span className="voice-choice-label">{t("voice.voiceStyle")}</span><div className="voice-choice-grid" role="radiogroup" aria-label={t("voice.voiceStyle")}>{voiceStyles.map((value) => <button key={value} type="button" role="radio" aria-checked={voiceStyle === value} className={voiceStyle === value ? "is-selected" : ""} onClick={() => setVoiceStyle(value)}>{t(`voice.style.${value}`)}{voiceStyle === value && <Check size={14} />}</button>)}</div></div>
            <div className="voice-choice-group"><span className="voice-choice-label">{t("voice.speakingStyle")}</span><div className="voice-choice-grid" role="radiogroup" aria-label={t("voice.speakingStyle")}>{speakingStyles.map((value) => <button key={value} type="button" role="radio" aria-checked={speakingStyle === value} className={speakingStyle === value ? "is-selected" : ""} onClick={() => setSpeakingStyle(value)}>{t(`voice.speaking.${value}`)}{speakingStyle === value && <Check size={14} />}</button>)}</div></div>
            <label className="voice-project-field"><span>{t("voice.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("voice.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
            <details className="voice-optional-controls"><summary>{t("voice.moreOptions")}</summary><label className="voice-instructions-field"><span>{t("voice.instructions")}</span><textarea value={instructions} onChange={(event) => setInstructions(event.target.value)} maxLength={3000} placeholder={t("voice.instructionsPlaceholder")} /></label></details>
          </section>

          <div className="voice-generate-column">
            {(error || isFailure) && <div className={`voice-alert ${unavailable ? "is-unavailable" : ""}`} role="alert"><AlertCircle size={16} /><span>{error || (unavailable ? t("voice.unavailable") : unsupportedLanguage ? t("voice.unsupportedLanguage") : cancelled ? t("voice.cancelled") : t("voice.failedSafe"))}</span></div>}
            {unavailable && <div className="voice-disabled-note"><span className="voice-disabled-dot" /><div><strong>{t("voice.unavailableTitle")}</strong><p>{t("voice.unavailableDescription")}</p></div></div>}
            <button className="voice-generate-action" type="submit" disabled={working || unavailable || text.trim().length < 1}><span>{unavailable ? t("voice.unavailableButton") : working ? t("voice.working") : t("voice.generate")}</span><Volume2 size={18} /></button>
            <p className="voice-generate-note"><Clock3 size={13} /> {t("voice.generateNote")}</p>
          </div>
        </form>
      )}

      <section className="voice-recent-section" aria-labelledby="voice-recent-title">
        <div className="voice-section-heading"><div><p className="section-eyebrow">03 / {t("voice.workflow.listen")}</p><h2 id="voice-recent-title">{t("voice.recentTitle")}</h2><p>{t("voice.recentSubtitle")}</p></div><Link href="/assets?type=audio" className="voice-text-link">{t("voice.openAssets")} <span aria-hidden="true">→</span></Link></div>
        {loadingRecent ? <div className="voice-recent-loading"><LoaderCircle size={18} /><span>{t("voice.recentLoading")}</span></div> : recentAssets.length === 0 ? <div className="voice-recent-empty"><div className="voice-empty-icon"><FileAudio size={22} /></div><div><h3>{t("voice.recentEmptyTitle")}</h3><p>{t("voice.recentEmptyDescription")}</p></div></div> : <div className="voice-recent-list">{recentAssets.map((asset) => <article className="voice-recent-item" key={asset.id}><div className="voice-recent-icon"><FileAudio size={17} /></div><div className="voice-recent-copy"><strong title={asset.name}>{asset.name}</strong><span>{asset.projectName ?? t("voice.noProject")} · {formatCreatedAt(asset.createdAt, locale)}</span><small>{formatVoiceFileSize(asset.fileSizeBytes)}{asset.mimeType ? ` · ${asset.mimeType.replace("audio/", "").toUpperCase()}` : ""}</small></div>{asset.hasFile && asset.canPreview ? <VoiceAudioPlayer src={api.assetFileUrl(asset.id, true)} label={asset.name} compact /> : <span className="voice-audio-unavailable">{t("voice.audioUnavailable")}</span>}<a className="voice-download-icon" href={api.assetFileUrl(asset.id)} aria-label={`${t("voice.download")} ${asset.name}`}><Download size={15} /></a></article>)}</div>}
      </section>
      <p className="voice-studio-footnote">{t("voice.safetyNote")}</p>
    </div>
  );
}

export { VoiceAudioPlayer };
