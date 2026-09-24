"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";
import { CheckCircle2, Download, Headphones, LoaderCircle, Mic2, RefreshCw, Volume2, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type Project, type VoiceGenerationInput } from "@/lib/api";
import { canCancelVoiceJob, parseVoiceJobResult } from "@/lib/voiceStudioState";

const languages = ["en", "ar", "ku"] as const;
const voiceStyles = ["neutral", "warm", "professional", "storytelling"] as const;
const speakingStyles = ["conversational", "clear", "expressive", "calm"] as const;

export function VoiceStudioView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [text, setText] = useState("");
  const [language, setLanguage] = useState<VoiceGenerationInput["language"]>("en");
  const [voiceStyle, setVoiceStyle] = useState<string>("neutral");
  const [speakingStyle, setSpeakingStyle] = useState<string>("clear");
  const [instructions, setInstructions] = useState("");
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [working, setWorking] = useState(false);
  const [loadingProjects, setLoadingProjects] = useState(true);
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

  // Synchronize the optional project picker with the authenticated workspace.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); }, [loadProjects]);

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
  }

  const result = useMemo(() => parseVoiceJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";
  const unavailable = current?.errorCode === "VOICE_PROVIDER_UNAVAILABLE";

  return <div className="voice-studio-page">
    <div className="voice-studio-header">
      <div><p className="section-eyebrow">{t("voice.eyebrow")}</p><h1>{t("voice.title")}</h1><p>{t("voice.subtitle")}</p></div>
      <span className="voice-studio-header-icon"><Volume2 size={25} /></span>
    </div>

    {!current || isFailure ? <form className="voice-studio-layout" onSubmit={(event) => void generate(event)}>
      <section className="account-card voice-studio-form-card">
        <div className="card-title"><span className="card-title-icon teal"><Mic2 size={17} /></span><div><h2>{t("voice.createTitle")}</h2><p>{t("voice.createSubtitle")}</p></div></div>
        <label className="voice-primary-field"><span>{t("voice.textLabel")}</span><textarea dir={language === "en" ? "ltr" : "rtl"} value={text} onChange={(event) => setText(event.target.value)} maxLength={10000} placeholder={t("voice.textPlaceholder")} aria-label={t("voice.textLabel")} required /><small>{text.length}/10000</small></label>
        <div className="voice-control-grid">
          <label className="field"><span>{t("voice.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as VoiceGenerationInput["language"])}>{languages.map((value) => <option key={value} value={value}>{t(`voice.language.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("voice.voiceStyle")}</span><select value={voiceStyle} onChange={(event) => setVoiceStyle(event.target.value)}>{voiceStyles.map((value) => <option key={value} value={value}>{t(`voice.style.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("voice.speakingStyle")}</span><select value={speakingStyle} onChange={(event) => setSpeakingStyle(event.target.value)}>{speakingStyles.map((value) => <option key={value} value={value}>{t(`voice.speaking.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("voice.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("voice.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        </div>
        <details className="voice-optional-controls"><summary>{t("voice.moreOptions")}</summary><label className="field"><span>{t("voice.instructions")}</span><textarea value={instructions} onChange={(event) => setInstructions(event.target.value)} maxLength={3000} placeholder={t("voice.instructionsPlaceholder")} /></label></details>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
        {isFailure && <div className={`form-error ${unavailable ? "voice-unavailable" : ""}`}><XCircle size={15} /> {unavailable ? t("voice.unavailable") : current?.errorMessage ?? t("voice.failedSafe")}</div>}
        <button className="primary-button voice-generate-button" type="submit" disabled={working || text.trim().length < 1}><Volume2 size={16} /> {working ? t("voice.working") : t("voice.generate")}</button>
      </section>
      <aside className="account-card voice-studio-guidance"><Headphones size={26} /><h2>{t("voice.guidanceTitle")}</h2><p>{t("voice.guidanceText")}</p><ul><li>{t("voice.guidanceOne")}</li><li>{t("voice.guidanceTwo")}</li><li>{t("voice.guidanceThree")}</li></ul></aside>
    </form> : <section className="account-card voice-generation-state" aria-live="polite">
      {isSuccess && result ? <><div className="voice-result-heading"><div><p className="section-eyebrow">{t("voice.resultEyebrow")}</p><h2>{t("voice.resultTitle")}</h2></div><span className="form-success"><CheckCircle2 size={16} /> {t("voice.savedToAssets")}</span></div><div className="voice-player"><audio controls preload="metadata" crossOrigin="use-credentials" src={api.assetFileUrl(result.assetId!, true)}><track kind="captions" /></audio></div><div className="voice-result-meta">{t(`voice.language.${result.language ?? language}`)} · {t(`voice.style.${result.voiceStyle ?? voiceStyle}`)} · {t(`voice.speaking.${result.speakingStyle ?? speakingStyle}`)}</div><div className="voice-result-actions"><a className="secondary-button" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("voice.download")}</a><Link className="secondary-button" href={`/assets?search=${encodeURIComponent(text.slice(0, 60))}`}>{t("voice.openAssets")}</Link><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("voice.createAnother")}</button></div></> : <><div className="voice-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("voice.progressEyebrow")}</p><h2>{t(`jobs.status${current?.status ?? "Queued"}`)}</h2><p className="voice-progress-copy">{t("voice.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{current?.progressPercent ?? 0}%</strong></div><div className="generation-progress-track"><span style={{ width: `${current?.progressPercent ?? 0}%` }} /></div>{canCancelVoiceJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("voice.cancel")}</button>}</>}
    </section>}
    <p className="voice-studio-footnote">{t("voice.safetyNote")}</p>
  </div>;
}
