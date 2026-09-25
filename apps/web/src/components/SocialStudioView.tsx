"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { Check, CheckCircle2, Copy, FileText, Image as ImageIcon, LoaderCircle, RefreshCw, Share2, ShieldCheck, Sparkles, WandSparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type Project, type SocialJobResult, type SocialPost, type StoredFile } from "@/lib/api";
import { canCancelSocialJob, displaySocialProgress, formatSocialPostForCopy, isSocialAssetSelectable, isSocialSourceReady, nextSocialPollDelay, normalizeSocialPreviewPlatform, parseSocialJobResult, shouldPollSocialJob, socialStudioState, type SocialPreviewPlatform } from "@/lib/socialStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type Language = "auto" | "en" | "ar" | "ku";
type Platform = SocialPreviewPlatform;
type SocialType = "auto" | "announcement" | "product_launch" | "promotion" | "educational" | "thought_leadership" | "company_update" | "event" | "community" | "general";
type Tone = "professional" | "friendly" | "persuasive" | "educational" | "playful" | "concise" | "thoughtful";

type SocialCopy = {
  draftOnly: string;
  noPublish: string;
  noSchedule: string;
  noCredentials: string;
  preview: string;
  notPublished: string;
  variant: string;
  caption: string;
  visualDirection: string;
  hashtags: string;
  reviewHint: string;
  startHint: string;
  briefLabel: string;
  sourceLabel: string;
  selected: string;
};

const socialCopy: Record<"en" | "ar" | "ku", SocialCopy> = {
  en: {
    draftOnly: "Draft workspace",
    noPublish: "No publishing",
    noSchedule: "No scheduling",
    noCredentials: "No social credentials",
    preview: "Preview, not a live post",
    notPublished: "Not published",
    variant: "Variant",
    caption: "Caption",
    visualDirection: "Visual direction",
    hashtags: "Hashtags",
    reviewHint: "Compare the copy, CTA, and visual direction before you take it anywhere.",
    startHint: "Your generated variants will appear here as clean, mobile-ready drafts.",
    briefLabel: "Campaign brief",
    sourceLabel: "Context & assets",
    selected: "selected",
  },
  ar: {
    draftOnly: "مساحة مسودات",
    noPublish: "لا يوجد نشر",
    noSchedule: "لا توجد جدولة",
    noCredentials: "لا توجد بيانات دخول اجتماعية",
    preview: "معاينة وليست منشورًا مباشرًا",
    notPublished: "لم يتم النشر",
    variant: "النسخة",
    caption: "التعليق",
    visualDirection: "التوجيه البصري",
    hashtags: "الوسوم",
    reviewHint: "قارن النص والدعوة والتوجيه البصري قبل استخدامه في أي مكان.",
    startHint: "ستظهر النسخ المنشأة هنا كمسودات واضحة ومناسبة للهاتف.",
    briefLabel: "موجز الحملة",
    sourceLabel: "السياق والأصول",
    selected: "محدد",
  },
  ku: {
    draftOnly: "شوێنی ڕەشنووس",
    noPublish: "بڵاوکردنەوە نییە",
    noSchedule: "خشتەکردن نییە",
    noCredentials: "زانیاری چوونەژوورەوەی سۆشیال نییە",
    preview: "پێشبینینە، پۆستی ڕاستەوخۆ نییە",
    notPublished: "بڵاونەکراوەتەوە",
    variant: "وەشان",
    caption: "دەقی پۆست",
    visualDirection: "ڕێنمایی بینراوی",
    hashtags: "هاشتاگەکان",
    reviewHint: "دەق، بانگەشە و ڕێنمایی بینراوی بەراورد بکە پێش ئەوەی لە شوێنێکی تر بەکاری بهێنیت.",
    startHint: "وەشانە دروستکراوەکان لێرە وەک ڕەشنووسی پاک و گونجاو بۆ مۆبایل دەردەکەون.",
    briefLabel: "کورتەی کەمپەین",
    sourceLabel: "کۆنتێکست و ئاسێتەکان",
    selected: "هەڵبژێردراو",
  },
};

const platformValues: Platform[] = ["multi", "instagram", "facebook", "linkedin", "x", "tiktok"];
const socialTypes: SocialType[] = ["auto", "announcement", "product_launch", "promotion", "educational", "thought_leadership", "company_update", "event", "community", "general"];
const tones: Tone[] = ["professional", "friendly", "persuasive", "educational", "playful", "concise", "thoughtful"];

export function SocialStudioView() {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const copy = socialCopy[locale];
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [assets, setAssets] = useState<Asset[]>([]);
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [selectedAssets, setSelectedAssets] = useState<string[]>([]);
  const [prompt, setPrompt] = useState("");
  const [socialType, setSocialType] = useState<SocialType>("auto");
  const [platform, setPlatform] = useState<Platform>("multi");
  const [tone, setTone] = useState<Tone>("professional");
  const [language, setLanguage] = useState<Language>("auto");
  const [audience, setAudience] = useState("");
  const [brandVoice, setBrandVoice] = useState("");
  const [callToAction, setCallToAction] = useState("");
  const [includeHashtags, setIncludeHashtags] = useState(true);
  const [includeEmojis, setIncludeEmojis] = useState(false);
  const [generateVariants, setGenerateVariants] = useState(true);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loadingInputs, setLoadingInputs] = useState(true);
  const [working, setWorking] = useState(false);
  const [pollRetry, setPollRetry] = useState(0);
  const [error, setError] = useState("");
  const [copyState, setCopyState] = useState("");

  const loadInputs = useCallback(async () => {
    if (!workspace) return;
    setLoadingInputs(true);
    try {
      const [active, archived, available, assetList] = await Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived"), api.listFiles(workspace.id), api.listAssets(workspace.id, { status: "Active", page: 1, pageSize: 100 })]);
      setProjects([...active, ...archived]);
      setFiles(available.filter((file) => extensions.includes(file.extension.toLowerCase())));
      setAssets(assetList.items.filter(isSocialAssetSelectable));
    } catch { setProjects([]); setFiles([]); setAssets([]); } finally { setLoadingInputs(false); }
  }, [workspace]);

  // Synchronize authenticated workspace choices into the form.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadInputs(); }, [loadInputs]);
  useEffect(() => {
    if (!current || !shouldPollSocialJob(current)) return;
    const jobId = current.id;
    let active = true;
    const timer = window.setTimeout(async () => {
      try { const next = await api.getGenerationJob(jobId); if (!active) return; setCurrent(next); setPollRetry(0); setError(""); }
      catch { if (!active) return; setPollRetry((attempt) => attempt + 1); setError(t("social.pollError")); }
    }, nextSocialPollDelay(current, pollRetry) ?? 700);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);

  function toggleFile(file: StoredFile) {
    if (!isSocialSourceReady(file)) return;
    setSelectedFiles((value) => value.includes(file.id) ? value.filter((id) => id !== file.id) : value.length >= 5 ? value : [...value, file.id]);
  }
  function toggleAsset(asset: Asset) {
    if (!isSocialAssetSelectable(asset)) return;
    setSelectedAssets((value) => value.includes(asset.id) ? value.filter((id) => id !== asset.id) : value.length >= 8 ? value : [...value, asset.id]);
  }
  async function create(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || prompt.trim().length < 3) { setError(t("social.required")); return; }
    setWorking(true); setError(""); setCopyState("");
    try {
      const job = await api.createSocialGenerationJob({ workspaceId: workspace.id, projectId: projectId || null, prompt: prompt.trim(), socialType, platform, tone, language, audience: audience.trim() || null, brandVoice: brandVoice.trim() || null, callToAction: callToAction.trim() || null, includeHashtags, includeEmojis, generateVariants, assetIds: selectedAssets, attachmentIds: selectedFiles });
      setCurrent(job); setPollRetry(0);
    } catch { setError(t("social.createError")); } finally { setWorking(false); }
  }
  async function cancel() {
    if (!current || !canCancelSocialJob(current)) return;
    setWorking(true); setError("");
    try { await api.cancelGenerationJob(current.id); setCurrent(await api.getGenerationJob(current.id)); } catch { setError(t("social.cancelError")); } finally { setWorking(false); }
  }
  async function copyPost(post: SocialPost) {
    try { await navigator.clipboard.writeText(formatSocialPostForCopy(post)); setCopyState(String(post.order)); window.setTimeout(() => setCopyState(""), 1800); }
    catch { setError(t("social.copyError")); }
  }
  function createAnother() { setCurrent(null); setError(""); setCopyState(""); setPollRetry(0); }

  const result = parseSocialJobResult(current);
  const state = socialStudioState(current, result);
  const readyFiles = files.filter(isSocialSourceReady);
  const progress = displaySocialProgress(current);
  const statusKey = current?.status ?? "Queued";
  return <div className="social-studio-page">
    <div className="social-studio-header">
      <div><p className="section-eyebrow">{t("social.eyebrow")}</p><h1>{t("social.title")}</h1><p>{t("social.subtitle")}</p></div>
      <div className="social-studio-header-tools"><span className="social-draft-badge"><ShieldCheck size={14} /> {copy.draftOnly}</span><span className="social-studio-header-icon"><Share2 size={26} /></span></div>
    </div>
    {!current ? <form className="social-studio-layout" onSubmit={(event) => void create(event)}>
      <section className="account-card social-studio-form-card">
        <div className="social-form-intro"><div className="social-form-intro-mark"><WandSparkles size={17} /></div><div><p className="section-eyebrow">{copy.briefLabel}</p><h2>{t("social.createTitle")}</h2><p>{t("social.createSubtitle")}</p></div></div>
        <label className="social-prompt-field"><span>{t("social.promptLabel")}</span><textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} maxLength={6000} placeholder={t("social.promptPlaceholder")} required /><small>{prompt.length}/6000</small></label>
        <div className="social-section-label"><span>01</span><div><strong>{t("social.platform")}</strong><small>{t("social.type")} · {t("social.tone")}</small></div></div>
        <div className="social-control-grid"><label className="field"><span>{t("social.platform")}</span><select value={platform} onChange={(event) => setPlatform(event.target.value as Platform)}>{platformValues.map((value) => <option key={value} value={value}>{t(`social.platform.${value}`)}</option>)}</select></label><label className="field"><span>{t("social.type")}</span><select value={socialType} onChange={(event) => setSocialType(event.target.value as SocialType)}>{socialTypes.map((value) => <option key={value} value={value}>{t(`social.type.${value}`)}</option>)}</select></label><label className="field"><span>{t("social.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as Tone)}>{tones.map((value) => <option key={value} value={value}>{t(`social.tone.${value}`)}</option>)}</select></label><label className="field"><span>{t("social.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as Language)}><option value="auto">{t("social.language.auto")}</option><option value="en">{t("social.language.en")}</option><option value="ar">{t("social.language.ar")}</option><option value="ku">{t("social.language.ku")}</option></select></label><label className="field social-project-field"><span>{t("social.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingInputs}><option value="">{t("social.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label></div>
        <details className="social-advanced"><summary><span>{t("social.advanced")}</span><small>{t("social.audience")}, {t("social.brandVoice")}, {t("social.callToAction")}</small></summary><div className="social-advanced-grid"><label className="field"><span>{t("social.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("social.audiencePlaceholder")} /></label><label className="field"><span>{t("social.brandVoice")}</span><input value={brandVoice} onChange={(event) => setBrandVoice(event.target.value)} maxLength={1000} placeholder={t("social.brandVoicePlaceholder")} /></label><label className="field"><span>{t("social.callToAction")}</span><input value={callToAction} onChange={(event) => setCallToAction(event.target.value)} maxLength={400} placeholder={t("social.callToActionPlaceholder")} /></label><div className="social-checkboxes"><label className="social-checkbox"><input type="checkbox" checked={includeHashtags} onChange={(event) => setIncludeHashtags(event.target.checked)} /> <span>{t("social.includeHashtags")}</span></label><label className="social-checkbox"><input type="checkbox" checked={includeEmojis} onChange={(event) => setIncludeEmojis(event.target.checked)} /> <span>{t("social.includeEmojis")}</span></label><label className="social-checkbox"><input type="checkbox" checked={generateVariants} onChange={(event) => setGenerateVariants(event.target.checked)} /> <span>{t("social.generateVariants")}</span></label></div></div></details>
        <div className="social-section-label"><span>02</span><div><strong>{copy.sourceLabel}</strong><small>{t("social.sourceFilesHint")} · {t("social.assetsHint")}</small></div></div>
        <div className="social-source-columns"><SelectionList title={t("social.sourceFiles")} hint={t("social.sourceFilesHint")} count={`${selectedFiles.length}/5`} loading={loadingInputs} empty={readyFiles.length === 0 ? t("social.noSourceFiles") : ""}>{readyFiles.map((file) => <label className={`social-selection-option ${selectedFiles.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selectedFiles.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={16} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span></label>)}</SelectionList><SelectionList title={t("social.assets")} hint={t("social.assetsHint")} count={`${selectedAssets.length}/8`} loading={loadingInputs} empty={assets.length === 0 ? t("social.noAssets") : ""}>{assets.map((asset) => <label className={`social-selection-option ${selectedAssets.includes(asset.id) ? "is-selected" : ""}`} key={asset.id}><input type="checkbox" checked={selectedAssets.includes(asset.id)} onChange={() => toggleAsset(asset)} />{asset.assetType === "image" ? <ImageIcon size={16} /> : <Share2 size={16} />}<span><strong>{asset.name}</strong><small>{t(`assets.type.${asset.assetType}`)}</small></span></label>)}</SelectionList></div>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}<button className="primary-button social-generate-button" type="submit" disabled={working || prompt.trim().length < 3}><Sparkles size={16} /> {working ? t("social.working") : t("social.generate")}<span>{generateVariants ? "· 3" : "· 1"}</span></button>
      </section>
      <aside className="social-compose-rail"><div className="social-guardrail-card"><div className="social-guardrail-icon"><ShieldCheck size={18} /></div><p className="section-eyebrow">{copy.preview}</p><h2>{copy.startHint}</h2><div className="social-guardrail-list"><span><CheckCircle2 size={14} /> {copy.noPublish}</span><span><CheckCircle2 size={14} /> {copy.noSchedule}</span><span><CheckCircle2 size={14} /> {copy.noCredentials}</span></div></div><div className="social-empty-preview"><div className="social-empty-preview-top"><span>{copy.preview}</span><span>{t("social.platform.multi")}</span></div><div className="social-empty-art"><WandSparkles size={22} /></div><div className="social-empty-lines"><i /><i /><i /></div><p>{copy.reviewHint}</p></div><Link className="secondary-button social-asset-link" href="/assets"><ImageIcon size={14} /> {t("social.openAssets")}</Link></aside>
    </form> : state === "failed" || state === "cancelled" ? <section className="account-card social-generation-state social-terminal-state" aria-live="polite"><XCircle size={28} /><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("social.failedSafe")}</h2><div className="social-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("social.createAnother")}</button><Link className="secondary-button" href="/assets">{t("social.openAssets")}</Link></div></section> : state === "succeeded" && result ? <SocialResult result={result} copy={copy} copyState={copyState} onCopy={(post) => void copyPost(post)} onCreateAnother={createAnother} t={t} /> : state === "completed-unavailable" ? <section className="account-card social-generation-state social-terminal-state"><RefreshCw size={28} /><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("social.completedLoadError")}</h2><p>{t("social.completedLoadHint")}</p><div className="social-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("social.createAnother")}</button><Link className="secondary-button" href="/assets">{t("social.openAssets")}</Link></div></section> : <section className="account-card social-generation-state" aria-live="polite"><div className="image-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("social.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2><p className="image-progress-copy">{t("social.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{progress}%</strong></div><div className="generation-progress-track"><span style={{ width: `${progress}%` }} /></div>{error && <div className="form-error"><XCircle size={15} /> {error}</div>}{canCancelSocialJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("social.cancel")}</button>}</section>}
    <p className="social-studio-footnote"><ShieldCheck size={13} /> {t("social.safetyNote")} · {copy.noSchedule} · {copy.noCredentials}</p>
  </div>;
}

function SelectionList({ title, hint, count, loading, empty, children }: { title: string; hint: string; count: string; loading: boolean; empty: string; children: React.ReactNode }) {
  return <div className="social-selection-list"><div className="social-selection-heading"><div><h3>{title}</h3><p>{hint}</p></div><strong>{count}</strong></div>{loading ? <p className="usage-empty">Loading...</p> : empty ? <p className="usage-empty">{empty}</p> : children}</div>;
}

function SocialResult({ result, copy, copyState, onCopy, onCreateAnother, t }: { result: SocialJobResult; copy: SocialCopy; copyState: string; onCopy: (post: SocialPost) => void; onCreateAnother: () => void; t: (key: string, values?: Record<string, string>) => string }) {
  const platform = normalizeSocialPreviewPlatform(result.platform);
  const platformLabel = t(`social.platform.${platform}`);
  const posts = result.posts ?? [];
  return <section className="social-result-shell" aria-live="polite"><div className="social-result-topbar"><div><p className="section-eyebrow">{t("social.resultEyebrow")}</p><h2>{result.title || t("social.resultTitle")}</h2><p className="social-result-meta">{t("social.postCount", { count: String(result.postCount ?? posts.length) })} · {platformLabel}</p></div><div className="social-result-status"><CheckCircle2 size={15} /> {t("social.savedToAssets")}</div></div><div className="social-review-banner"><div><strong>{copy.preview}</strong><span>{copy.reviewHint}</span></div><span className="social-not-live"><span className="social-live-dot" /> {copy.notPublished}</span></div><div className={`social-post-list social-post-list-${platform}`}>{posts.map((post) => <article className={`social-post-card social-preview-${platform}`} key={post.order}><div className="social-preview-bar"><span className="social-preview-platform">{platformLabel}</span><span>{copy.variant} {post.order}</span><button className="secondary-button" type="button" onClick={() => onCopy(post)}>{copyState === String(post.order) ? <><Check size={14} /> {t("social.copied")}</> : <><Copy size={14} /> {t("social.copy")}</>}</button></div><div className="social-preview-identity"><span className="social-preview-avatar">T</span><div><strong>{copy.draftOnly}</strong><small>{copy.notPublished}</small></div><span className="social-preview-menu">•••</span></div><div className="social-preview-copy" dir="auto"><p className="social-preview-label">{copy.caption}</p><h3>{post.hook}</h3><p className="social-preview-body">{post.body}</p>{post.callToAction && <div className="social-preview-cta"><span>{t("social.callToAction")}</span><strong dir="auto">{post.callToAction}</strong></div>}{post.hashtags?.length ? <div className="social-preview-hashtags"><span>{copy.hashtags}</span><p>{post.hashtags.join(" ")}</p></div> : null}</div>{post.visualDirection && <div className="social-preview-detail" dir="auto"><span><ImageIcon size={13} /> {copy.visualDirection}</span><p>{post.visualDirection}</p></div>}{post.altText && <details className="social-post-detail"><summary>{t("social.altText")}</summary><p dir="auto">{post.altText}</p></details>}{post.assetRefs?.length ? <small className="social-post-assets">{t("social.usesAssets")}: {post.assetRefs.join(", ")}</small> : null}</article>)}</div><div className="social-result-actions"><Link className="secondary-button" href="/assets">{t("social.openAssets")}</Link><button className="primary-button" onClick={onCreateAnother}><RefreshCw size={15} /> {t("social.createAnother")}</button></div></section>;
}
