"use client";
import { localeTag } from "@/lib/i18n";

/* The private download endpoint requires the browser's authenticated session cookie. */
/* eslint-disable @next/next/no-img-element */

import Link from "next/link";
import { type KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ArrowUpRight,
  Check,
  CheckCircle2,
  ChevronDown,
  Download,
  Image as ImageIcon,
  LoaderCircle,
  Paperclip,
  Palette,
  RefreshCw,
  Sparkles,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { AssetDetail, type AssetDetailLabels } from "@/components/AssetDetail";
import { AccessibleProgressBar } from "@/components/AccessibleProgressBar";
import { useLocale } from "@/components/LocaleProvider";
import { useSearchParams } from "next/navigation";
import { ApiError, api, type Asset, type GenerationJob, type ImageGenerationInput, type Project } from "@/lib/api";
import { localizedImageErrorMessage } from "@/lib/imageStudioErrors";
import { nextStudioChoiceIndex } from "@/lib/studioChoiceNavigation";
import { canCancelImageJob, clearImageActiveJobId, isRestorableImageJob, mergeImageJobUpdate, nextImagePollDelay, nextImageRestoreDelay, parseImageJobResult, persistImageActiveJobId, readImageActiveJobId, shouldClearImageJobAfterPollError, shouldPollImageJob, shouldResetImageWorkspaceState, shouldRetryImageRestore, subscribeImageActiveJobChanges } from "@/lib/imageStudioState";

const styles = ["auto", "photorealistic", "product", "illustration", "3d", "minimal", "poster", "social_media"] as const;
const aspects = ["square", "portrait", "landscape"] as const;
const qualities = ["standard", "high"] as const;
const purposes = ["product", "marketing", "social", "portrait", "illustration", "background", "other"] as const;

type Purpose = (typeof purposes)[number];

const purposeStyles: Record<Purpose, (typeof styles)[number]> = {
  product: "product",
  marketing: "poster",
  social: "social_media",
  portrait: "photorealistic",
  illustration: "illustration",
  background: "minimal",
  other: "auto",
};

export type ImageFailureCanvasLabels = Readonly<{
  eyebrow: string;
  title: string;
  message: string;
  retry: string;
  retrying: string;
}>;

/**
 * Terminal Image Studio failure state, including upload/save failures such as
 * `IMAGE_OUTPUT_STORAGE_FAILED`, with its recovery action.
 *
 * The retry control stays focusable while busy (`aria-disabled` instead of the `disabled`
 * attribute, which drops browser focus and with it the screen reader's place) and exposes the
 * in-flight state through `aria-busy` plus a swapped accessible name. The reason for the failure
 * is associated with the action through `aria-describedby`, so the user learns whether retrying
 * the upload is the correct recovery step.
 */
export function ImageFailureCanvas({ labels, working, onRetry }: Readonly<{ labels: ImageFailureCanvasLabels; working: boolean; onRetry: () => void }>) {
  const messageId = "image-failure-message";
  return <div className="image-failure-canvas" aria-busy={working}>
    <div className="image-failure-icon"><XCircle size={24} /></div>
    <p className="image-canvas-eyebrow">{labels.eyebrow}</p>
    <h2>{labels.title}</h2>
    <p id={messageId}>{labels.message}</p>
    <button className="image-retry-button" type="button" onClick={onRetry} aria-disabled={working || undefined} aria-busy={working} aria-describedby={messageId}>
      <RefreshCw size={15} /> {working ? labels.retrying : labels.retry}
    </button>
  </div>;
}

export type ImageGeneratingCanvasLabels = Readonly<{
  eyebrow: string;
  title: string;
  message: string;
  progress: string;
  cancel: string;
}>;

/**
 * In-flight Image Studio state and its cancellation control.
 *
 * The cancel control follows the same busy-state contract as the failure canvas retry: it stays
 * focusable and reports `aria-busy` while the request is in flight, because the `disabled`
 * attribute removes the focused button from the accessibility tree and leaves a keyboard or screen
 * reader user at the top of the document with no idea what happened to the control they pressed.
 * The control keeps its name, since a cancellation that is being sent is never a new start.
 * Activation is made idempotent by {@link ImageStudioContent.cancel}, which refuses a second
 * request for a job that has already been asked to stop.
 */
export function ImageGeneratingCanvas({ labels, percent, canCancel, working, onCancel }: Readonly<{ labels: ImageGeneratingCanvasLabels; percent: number | null; canCancel: boolean; working: boolean; onCancel: () => void }>) {
  return <div className="image-generating-canvas">
    <div className="image-generating-spinner"><LoaderCircle size={26} aria-hidden="true" /></div>
    <p className="image-canvas-eyebrow">{labels.eyebrow}</p>
    <h2>{labels.title}</h2>
    <p>{labels.message}</p>
    <div className="image-progress-meta"><span>{labels.progress}</span><strong>{percent ?? 0}%</strong></div>
    <AccessibleProgressBar className="image-progress-track" label={labels.progress} value={percent} />
    {canCancel && <button className="image-cancel-button" type="button" onClick={onCancel} aria-disabled={working || undefined} aria-busy={working}>
      <XCircle size={15} /> {labels.cancel}
    </button>}
  </div>;
}

/**
 * Arrow-key model for a prompt preset choice group. In a radiogroup the axis keys move the
 * selection itself: one key press changes the preset, moves focus to the newly selected radio,
 * and (for the purpose group) keeps the preset's style side effect wired through
 * {@link ImageStudioContent.choosePurpose}. The group's accessible name and hint come from the
 * localized label elements referenced by `aria-labelledby` and `aria-describedby`, so the model
 * is identical in English, Arabic, and Kurdish Sorani.
 */
function handleImageChoiceKeyDown<T extends string>(event: KeyboardEvent<HTMLButtonElement>, options: readonly T[], value: T, onChange: (next: T) => void) {
  const nextIndex = nextStudioChoiceIndex(options.indexOf(value), event.key, options.length);
  if (nextIndex === null) return;
  event.preventDefault();
  onChange(options[nextIndex]);
  event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[nextIndex]?.focus();
}

export function ImageStudioView() {
  const { user, workspace } = useAuth();
  // Clear all private component state and in-flight timers across identity boundaries.
  return <ImageStudioContent key={`${user?.id ?? "anonymous"}:${workspace?.id ?? "none"}`} />;
}

function ImageStudioContent() {
  const { user, workspace } = useAuth();
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [recentAssets, setRecentAssets] = useState<Asset[]>([]);
  const [selectedAsset, setSelectedAsset] = useState<Asset | null>(null);
  const [description, setDescription] = useState("");
  const [purpose, setPurpose] = useState<Purpose>("product");
  const [style, setStyle] = useState<string>(purposeStyles.product);
  const [aspectRatio, setAspectRatio] = useState<string>("square");
  const [quality, setQuality] = useState<string>("standard");
  const [title, setTitle] = useState("");
  const [mood, setMood] = useState("");
  const [background, setBackground] = useState("");
  const [textInImage, setTextInImage] = useState("");
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [pollRetry, setPollRetry] = useState(0);
  // The poll loop must keep running whether or not the last frame changed anything: a rejected
  // stale frame leaves the stored job untouched, and a loop that is rescheduled by that state
  // change alone would stop polling a generation that is still running.
  const [pollTick, setPollTick] = useState(0);
  const [sessionDenied, setSessionDenied] = useState(false);
  const sessionDeniedRef = useRef(false);
  const restoreJobId = useRef<string | null>(null);
  const observedWorkspaceId = useRef<string | null>(workspace?.id ?? null);
  const workspaceGeneration = useRef(0);
  const [working, setWorking] = useState(false);
  const [loadingProjects, setLoadingProjects] = useState(true);
  const [loadingRecent, setLoadingRecent] = useState(true);
  const [error, setError] = useState("");
  // Bumped whenever the active-job pointer for this identity changes, including when the write
  // comes from a request that outlived the instance that issued it.
  const [pointerRevision, setPointerRevision] = useState(0);

  useEffect(() => {
    const userId = user?.id;
    const workspaceId = workspace?.id;
    if (!userId || !workspaceId) return;
    // Only the identity that owns the pointer may adopt it, so a write for a previous workspace
    // or a previous account never starts a read here.
    return subscribeImageActiveJobChanges((changedUserId, changedWorkspaceId) => {
      if (changedUserId !== userId || changedWorkspaceId !== workspaceId) return;
      setPointerRevision((revision) => revision + 1);
    });
  }, [user?.id, workspace?.id]);

  const loadProjects = useCallback(async () => {
    if (!user?.id || !workspace || sessionDeniedRef.current) return;
    const workspaceId = workspace.id;
    setLoadingProjects(true);
    try {
      const [active, archived] = await Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived")]);
      if (sessionDeniedRef.current || observedWorkspaceId.current !== workspaceId) return;
      setProjects([...active, ...archived]);
    } catch {
      if (sessionDeniedRef.current || observedWorkspaceId.current !== workspaceId) return;
      setProjects([]);
    } finally {
      if (!sessionDeniedRef.current && observedWorkspaceId.current === workspaceId) setLoadingProjects(false);
    }
  }, [workspace, user?.id]);

  const loadRecent = useCallback(async () => {
    if (!user?.id || !workspace || sessionDeniedRef.current) return;
    const workspaceId = workspace.id;
    setLoadingRecent(true);
    try {
      const result = await api.listAssets(workspace.id, { assetType: "image", status: "Active", sort: "recent", page: 1, pageSize: 6 });
      if (sessionDeniedRef.current || observedWorkspaceId.current !== workspaceId) return;
      setRecentAssets(result.items);
    } catch {
      if (sessionDeniedRef.current || observedWorkspaceId.current !== workspaceId) return;
      setRecentAssets([]);
    } finally {
      if (!sessionDeniedRef.current && observedWorkspaceId.current === workspaceId) setLoadingRecent(false);
    }
  }, [workspace, user?.id]);

  // Loading remote projects and assets after the workspace changes is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); }, [loadProjects]);
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadRecent(); }, [loadRecent]);

  // Workspace-scoped job, project, and asset state must not survive an ownership boundary.
  // The generation counter also fences responses from requests started in the previous workspace.
  useEffect(() => {
    const nextWorkspaceId = workspace?.id ?? null;
    if (observedWorkspaceId.current === nextWorkspaceId) return;
    const previousWorkspaceId = observedWorkspaceId.current;
    observedWorkspaceId.current = nextWorkspaceId;
    workspaceGeneration.current += 1;
    restoreJobId.current = null;
    if (previousWorkspaceId === null && nextWorkspaceId !== null && !shouldResetImageWorkspaceState(current, nextWorkspaceId)) return;
    setCurrent(null);
    setSelectedAsset(null);
    setProjects([]);
    setRecentAssets([]);
    setLoadingProjects(true);
    setLoadingRecent(true);
    setProjectId("");
    setPollRetry(0);
    setPollTick(0);
    setWorking(false);
    setError("");
  }, [current, workspace?.id]);

  useEffect(() => {
    if (!user?.id || !workspace || current || sessionDeniedRef.current) return;
    const storedJobId = readImageActiveJobId(user.id, workspace.id);
    if (!storedJobId) return;
    restoreJobId.current = storedJobId;
    const workspaceVersion = workspaceGeneration.current;
    let active = true;
    let retryTimer = 0;

    // The pointer names a generation that is normally still running server-side, so a failed read
    // is not evidence that the job disappeared. It is retried inside the polling budget, the
    // pointer is kept, and only an exhausted budget reports the missing progress. A single
    // unretried read used to strand the user on an empty canvas that announced nothing about a
    // job the workspace is still paying for.
    const readStoredJob = (attempt: number) => {
      void api.getGenerationJob(storedJobId).then((job) => {
        if (!active || workspaceGeneration.current !== workspaceVersion || restoreJobId.current !== storedJobId) return;
        if (!isRestorableImageJob(job, workspace.id)) {
          clearImageActiveJobId(user.id, workspace.id);
          restoreJobId.current = null;
          return;
        }
        setCurrent((previous) => mergeImageJobUpdate(previous, job));
        setPollRetry(0);
        setError("");
      }).catch((cause: unknown) => {
        if (!active || workspaceGeneration.current !== workspaceVersion || restoreJobId.current !== storedJobId) return;
        const status = cause instanceof ApiError ? cause.status : null;
        const code = cause instanceof ApiError ? cause.code : null;
        if (shouldClearImageJobAfterPollError(status)) {
          clearImageActiveJobId(user.id, workspace.id);
          restoreJobId.current = null;
          if (status === 401) {
            sessionDeniedRef.current = true;
            setSessionDenied(true);
            setProjects([]);
            setRecentAssets([]);
            setSelectedAsset(null);
            setLoadingProjects(false);
            setLoadingRecent(false);
            setError(localizedImageErrorMessage(code, t, "image.pollError"));
          }
          return;
        }
        if (!shouldRetryImageRestore(status, attempt)) {
          setError(localizedImageErrorMessage(code, t, "image.pollError"));
          return;
        }
        retryTimer = window.setTimeout(() => readStoredJob(attempt + 1), nextImageRestoreDelay(attempt));
      });
    };
    readStoredJob(0);
    return () => { active = false; window.clearTimeout(retryTimer); };
  }, [user?.id, workspace, current, pointerRevision, t]);

  useEffect(() => {
    const workspaceId = workspace?.id;
    if (!user?.id || !workspaceId || sessionDeniedRef.current || !current || !shouldPollImageJob(current)) return;
    let active = true;
    const jobId = current.id;
    const workspaceVersion = workspaceGeneration.current;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (active && workspaceGeneration.current === workspaceVersion && current.id === jobId) {
          setCurrent((previous) => mergeImageJobUpdate(previous, next));
          setPollRetry(0);
          setError("");
          setPollTick((tick) => tick + 1);
        }
      } catch (caught) {
        if (active && workspaceGeneration.current === workspaceVersion && current.id === jobId) {
          if (caught instanceof ApiError && shouldClearImageJobAfterPollError(caught.status)) {
            clearImageActiveJobId(user.id, workspaceId);
            restoreJobId.current = null;
            if (caught.status === 401) {
              // Revoked authentication is terminal: no new private GET or POST in this mount.
              sessionDeniedRef.current = true;
              setSessionDenied(true);
              setProjects([]);
              setRecentAssets([]);
              setSelectedAsset(null);
              setLoadingProjects(false);
              setLoadingRecent(false);
            }
            setCurrent(null);
            setPollRetry(0);
            setError(localizedImageErrorMessage(caught.code, t, "image.pollError"));
            return;
          }
          setPollRetry((attempt) => attempt + 1);
          setError(localizedImageErrorMessage(caught instanceof ApiError ? caught.code : null, t, "image.pollError"));
        }
      }
    }, nextImagePollDelay(current, pollRetry) ?? 650);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, pollTick, t, user?.id, workspace?.id]);

  async function startGeneration() {
    // The retry control stays focusable while busy, so activation can arrive again before the
    // first request settles; a second submission would create a duplicate active studio job.
    if (working) return;
    if (sessionDenied || sessionDeniedRef.current || !user?.id || !workspace || description.trim().length < 3) {
      setError(t("image.descriptionRequired"));
      return;
    }
    setWorking(true);
    setError("");
    setPollRetry(0);
    restoreJobId.current = null;
    const requestWorkspaceVersion = workspaceGeneration.current;
    const input: ImageGenerationInput = {
      workspaceId: workspace.id,
      projectId: projectId || null,
      description: description.trim(),
      style,
      aspectRatio,
      quality,
      title: title.trim() || null,
      mood: mood.trim() || null,
      background: background.trim() || null,
      textInImage: textInImage.trim() || null,
    };
    try {
      const job = await api.createImageGenerationJob(input);
      if (workspaceGeneration.current === requestWorkspaceVersion) {
        persistImageActiveJobId(user.id, workspace.id, job.id);
        setCurrent((previous) => mergeImageJobUpdate(previous, job));
      }
    } catch (caught) {
      if (workspaceGeneration.current === requestWorkspaceVersion) setError(localizedImageErrorMessage(caught instanceof ApiError ? caught.code : null, t, "image.createError"));
    } finally {
      if (workspaceGeneration.current === requestWorkspaceVersion) setWorking(false);
    }
  }

  async function cancel() {
    // The cancel control stays focusable while a request is in flight, so activation can arrive
    // again before the first one settles. The job's own state decides whether a request is still
    // meaningful: a job that already asked to stop must not be asked twice, and a job the worker
    // has settled can no longer be cancelled at all.
    if (working) return;
    const job = current;
    if (!user?.id || sessionDeniedRef.current || !job || !canCancelImageJob(job)) return;
    const requestWorkspaceVersion = workspaceGeneration.current;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(job.id);
      const next = await api.getGenerationJob(job.id);
      if (workspaceGeneration.current === requestWorkspaceVersion) {
        setCurrent((previous) => previous?.id === job.id ? mergeImageJobUpdate(previous, next) : previous);
      }
    } catch (caught) {
      if (workspaceGeneration.current === requestWorkspaceVersion) setError(localizedImageErrorMessage(caught instanceof ApiError ? caught.code : null, t, "image.cancelError"));
    } finally {
      if (workspaceGeneration.current === requestWorkspaceVersion) setWorking(false);
    }
  }

  function choosePurpose(nextPurpose: Purpose) {
    setPurpose(nextPurpose);
    setStyle(purposeStyles[nextPurpose]);
  }

  function createAnother() {
    restoreJobId.current = null;
    if (user?.id && workspace) clearImageActiveJobId(user.id, workspace.id);
    setCurrent(null);
    setError("");
  }

  const result = useMemo(() => parseImageJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled" || (current?.status === "Succeeded" && !result);
  const failureMessage = current?.status === "Succeeded"
    ? t("image.resultUnavailable")
    : localizedImageErrorMessage(current?.errorCode, t, "image.failedText");
  const detailLabels = useMemo<AssetDetailLabels>(() => ({
    detailEyebrow: t("assets.detailEyebrow"),
    close: t("common.close"),
    structuredTitle: t("assets.structuredTitle"),
    structuredDescription: t("assets.structuredDescription"),
    previewLabel: t("assets.structuredTitle"),
    summaryLabel: t("assets.description"),
    download: t("assets.download"),
    open: t("assets.open"),
    edit: t("assets.rename"),
    type: t("assets.typeLabel"),
    typeValue: selectedAsset ? t(`assets.type.${selectedAsset.assetType}`) : "",
    created: t("assets.created"),
    project: t("assets.project"),
    noProject: t("assets.noProject"),
    file: t("assets.file"),
    notAvailable: t("assets.notAvailable"),
    source: t("assets.source"),
    manual: t("assets.manualSource"),
    description: t("assets.description"),
    representations: t("assets.representations"),
    studios: {
      image: t("navigation.imageStudio"),
      document: t("navigation.documentStudio"),
      presentation: t("navigation.presentationStudio"),
      research: t("navigation.researchStudio"),
      social: t("navigation.socialStudio"),
      voice: t("navigation.voiceStudio"),
      music: t("navigation.musicStudio"),
      movie: t("navigation.movieStudio"),
      system: t("assets.systemSource"),
    },
  }), [selectedAsset, t]);

  return <div className="image-studio-page">
    <header className="image-studio-header">
      <div className="image-studio-header-copy">
        <div className="image-studio-breadcrumb"><span className="image-studio-mark"><Palette size={15} /></span><span>{t("image.workspaceLabel")}</span><span className="image-studio-slash">/</span><strong>{t("image.title")}</strong></div>
        <p className="section-eyebrow">{t("image.eyebrow")}</p>
        <h1 aria-label={t("image.title")}>{t("image.workspaceTitle")}</h1>
        <p>{t("image.workspaceSubtitle")}</p>
      </div>
      <Link className="image-studio-library-link" href="/assets?assetType=image"><ImageIcon size={15} /> {t("image.openAssets")} <ArrowUpRight size={14} /></Link>
    </header>

    <div className="image-studio-workspace">
      <form className="image-creation-panel" onSubmit={(event) => { event.preventDefault(); void startGeneration(); }}>
        <div className="image-panel-heading"><div><p className="image-panel-kicker">{t("image.createTitle")}</p><h2>{t("image.promptLabel")}</h2></div><span className="image-panel-step">01</span></div>
        <label className="image-prompt-field"><span className="sr-only">{t("image.promptLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("image.promptPlaceholder")} aria-label={t("image.promptLabel")} required /><small>{description.length}/4000</small></label>
        <div className="image-panel-tools">
          <button className="image-attachment-button" type="button" disabled title={t("image.referenceUnavailable")}><Paperclip size={15} /> {t("image.attachReference")} <span>{t("image.unavailableTag")}</span></button>
          {/*
           * The project selector is a choice control whose options are fetched from the workspace,
           * so the studio's first render — and every render right after a workspace switch, which
           * sets `loadingProjects` back to true — happens while the list is in flight. The browser's
           * `disabled` attribute is therefore the wrong tool for that window, exactly as it is for
           * the retry and cancel controls above: it takes the focused selector out of the
           * accessibility tree and hands the caret back to the document, so a keyboard user either
           * never reaches the control or loses their place inside it without any announcement.
           * The selector instead stays tabbable and reports the transient state on itself through
           * `aria-busy` and `aria-disabled`, and while loading it offers only an unselectable
           * placeholder that says the list is loading: "No project" would assert an empty workspace
           * the client has not observed. Activation is refused by the change handler while loading,
           * the same idempotence contract {@link ImageStudioContent.cancel} uses.
           */}
          <label className="image-project-select"><span>{t("image.project")}</span><select value={projectId} onChange={(event) => { if (loadingProjects) return; setProjectId(event.target.value); }} aria-busy={loadingProjects} aria-disabled={loadingProjects || undefined}>{loadingProjects ? <option value="" disabled>{t("image.projectLoading")}</option> : <><option value="">{t("image.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</>}</select><ChevronDown size={13} /></label>
        </div>

        <div className="image-option-group"><div className="image-option-label"><span id="image-purpose-label">{t("image.purpose")}</span><small id="image-purpose-hint">{t("image.autoHint")}</small></div><div className="image-purpose-grid" role="radiogroup" aria-labelledby="image-purpose-label" aria-describedby="image-purpose-hint">{purposes.map((value) => <button key={value} type="button" role="radio" aria-checked={purpose === value} tabIndex={purpose === value ? 0 : -1} className={purpose === value ? "is-selected" : ""} onClick={() => choosePurpose(value)} onKeyDown={(event) => handleImageChoiceKeyDown(event, purposes, purpose, choosePurpose)}><span className={`image-purpose-dot is-${value}`} aria-hidden="true" />{t(`image.purpose.${value}`)}</button>)}</div></div>
        <div className="image-option-group"><div className="image-option-label"><span id="image-aspect-label">{t("image.aspectRatio")}</span></div><div className="image-segmented-control" role="radiogroup" aria-labelledby="image-aspect-label">{aspects.map((value) => <button key={value} type="button" role="radio" aria-checked={aspectRatio === value} tabIndex={aspectRatio === value ? 0 : -1} className={aspectRatio === value ? "is-selected" : ""} onClick={() => setAspectRatio(value)} onKeyDown={(event) => handleImageChoiceKeyDown(event, aspects, aspectRatio, setAspectRatio)}><span className={`image-format-icon is-${value}`} aria-hidden="true" />{t(`image.aspect.${value}`)}</button>)}</div></div>

        <details className="image-advanced-options"><summary>{t("image.moreOptions")} <ChevronDown size={15} /></summary><div className="image-advanced-grid"><label className="image-field"><span>{t("image.style")}</span><select value={style} onChange={(event) => setStyle(event.target.value)}>{styles.map((value) => <option key={value} value={value}>{t(`image.style.${value}`)}</option>)}</select></label><label className="image-field"><span>{t("image.quality")}</span><select value={quality} onChange={(event) => setQuality(event.target.value)}>{qualities.map((value) => <option key={value} value={value}>{t(`image.quality.${value}`)}</option>)}</select></label><label className="image-field"><span>{t("image.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={120} placeholder={t("image.titlePlaceholder")} /></label><label className="image-field"><span>{t("image.mood")}</span><input value={mood} onChange={(event) => setMood(event.target.value)} maxLength={120} placeholder={t("image.moodPlaceholder")} /></label><label className="image-field image-field-wide"><span>{t("image.background")}</span><input value={background} onChange={(event) => setBackground(event.target.value)} maxLength={240} placeholder={t("image.backgroundPlaceholder")} /></label><label className="image-field image-field-wide"><span>{t("image.textInImage")}</span><input value={textInImage} onChange={(event) => setTextInImage(event.target.value)} maxLength={500} placeholder={t("image.textInImagePlaceholder")} /></label></div></details>
        {error && <div className="image-panel-error" role="alert"><XCircle size={15} /> {error}</div>}
        <button className="image-generate-button" type="submit" disabled={working || sessionDenied || !user?.id || description.trim().length < 3}><span>{working ? <LoaderCircle className="image-button-spinner" size={16} /> : <Sparkles size={16} />}</span>{working ? t("image.working") : t("image.generate")}<span className="image-button-arrow">↗</span></button>
        <p className="image-panel-note"><CheckCircle2 size={13} /> {t("image.privateNote")}</p>
      </form>

      <main className="image-result-canvas" aria-live="polite">
        {!current && <div className="image-empty-canvas"><div className="image-canvas-orbit image-canvas-orbit-one" /><div className="image-canvas-orbit image-canvas-orbit-two" /><div className="image-canvas-core"><Sparkles size={25} /></div><p className="image-canvas-eyebrow">{t("image.canvasEmptyEyebrow")}</p><h2>{t("image.canvasEmptyTitle")}</h2><p>{t("image.canvasEmptyText")}</p><span className="image-canvas-hint">{t("image.canvasHint")}</span></div>}
        {current && !isSuccess && !isFailure && <ImageGeneratingCanvas labels={{ eyebrow: t("image.progressEyebrow"), title: t(`jobs.status${current.status}`), message: t("image.progressText"), progress: t("jobs.progress"), cancel: t("image.cancel") }} percent={current.progressPercent} canCancel={canCancelImageJob(current)} working={working} onCancel={() => void cancel()} />}
        {isFailure && <ImageFailureCanvas labels={{ eyebrow: t("image.failedEyebrow"), title: current?.status === "Cancelled" ? t("jobs.statusCancelled") : t("image.failedTitle"), message: failureMessage, retry: t("image.retry"), retrying: t("image.working") }} working={working} onRetry={() => void startGeneration()} />}
        {isSuccess && result && <div className="image-success-canvas"><div className="image-result-heading"><div><p className="image-canvas-eyebrow">{t("image.resultEyebrow")}</p><h2>{t("image.resultTitle")}</h2></div><span className="image-saved-badge"><Check size={14} /> {t("image.savedToAssets")}</span></div><div className={`image-result-frame is-${result.aspectRatio ?? "square"}`}><img crossOrigin="use-credentials" src={api.assetFileUrl(result.assetId!, true)} alt={description} /></div><div className="image-result-actions"><a className="image-result-action" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("image.download")}</a><Link className="image-result-action" href={`/assets?search=${encodeURIComponent(title || "Generated image")}`}><ImageIcon size={15} /> {t("image.openAssets")}</Link><button className="image-result-action is-primary" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("image.createAnother")}</button></div></div>}
      </main>

      <aside className="image-recent-panel">
        <div className="image-recent-heading"><div><p className="image-panel-kicker">{t("image.recentEyebrow")}</p><h2>{t("image.recentTitle")}</h2></div><Link href="/assets?assetType=image" aria-label={t("image.openAssets")}><ArrowUpRight size={16} /></Link></div>
        {loadingRecent ? <div className="image-recent-loading"><span className="image-mini-spinner" /></div> : recentAssets.length === 0 ? <div className="image-recent-empty"><ImageIcon size={21} /><p>{t("image.recentEmpty")}</p><small>{t("image.recentEmptyText")}</small></div> : <div className="image-recent-list">{recentAssets.map((asset) => <button key={asset.id} className="image-recent-item" type="button" onClick={() => setSelectedAsset(asset)}><span className="image-recent-thumb">{asset.hasFile && asset.canPreview ? <img src={api.assetFileUrl(asset.id, true)} alt="" loading="lazy" /> : <ImageIcon size={18} />}</span><span className="image-recent-copy"><strong>{asset.name}</strong><small>{asset.projectName || t("assets.workspaceLevel")}</small><time dateTime={asset.createdAt}>{new Intl.DateTimeFormat(localeTag(locale), { month: "short", day: "numeric" }).format(new Date(asset.createdAt))}</time></span><ArrowUpRight className="image-recent-arrow" size={14} /></button>)}</div>}
        <div className="image-recent-footer"><CheckCircle2 size={14} /><span>{t("image.libraryNote")}</span></div>
      </aside>
    </div>

    <p className="image-studio-footnote">{t("image.safetyNote")}</p>
    {selectedAsset && <AssetDetail asset={selectedAsset} locale={locale} labels={detailLabels} onClose={() => setSelectedAsset(null)} onEdit={() => setSelectedAsset(null)} />}
  </div>;
}
