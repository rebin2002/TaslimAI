"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowRight, ArrowUpRight, FolderKanban, LibraryBig, LoaderCircle, MessageSquare, Sparkles } from "lucide-react";
import { ProtectedPage } from "@/components/ProtectedPage";
import { HomeComposer } from "@/components/HomeComposer";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { StudioChooser } from "@/components/StudioChooser";
import { api, type ActivityItem, type Asset, type Conversation, type Project } from "@/lib/api";
import { buildHomeRecentItems, hasInFlightActivity, type HomeRecentItem } from "@/lib/homeDashboardState";

const localeMap = { en: "en-US", ar: "ar", ku: "ku-Arab" } as const;
const promptChips = [
  { labelKey: "home.promptCampaign" },
  { labelKey: "home.promptResearch" },
  { labelKey: "home.promptPresentation" },
  { labelKey: "home.promptImage" },
] as const;
type DashboardData = { projects: Project[]; conversations: Conversation[]; activity: ActivityItem[]; assets: Asset[] };

function formatDate(value: string, locale: keyof typeof localeMap) {
  return new Intl.DateTimeFormat(localeMap[locale], { month: "short", day: "numeric" }).format(new Date(value));
}

export default function HomePage() {
  return <ProtectedPage><HomeDashboard /></ProtectedPage>;
}

function HomeDashboard() {
  const { t, locale } = useLocale();
  const { user, workspace } = useAuth();
  const router = useRouter();
  const [idea, setIdea] = useState("");
  const [projectId, setProjectId] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [asking, setAsking] = useState(false);
  const [dashboard, setDashboard] = useState<DashboardData | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [askError, setAskError] = useState("");

  const loadDashboard = useCallback(async () => {
    if (!workspace) return;
    setLoading(true);
    try {
      const [projects, conversations, activityResult, assetsResult] = await Promise.all([
        api.listProjects(workspace.id, "Active"),
        api.listConversations(workspace.id),
        api.listActivity(workspace.id, 1, 12),
        api.listAssets(workspace.id, { status: "Active", page: 1, pageSize: 12 }),
      ]);
      setDashboard({ projects, conversations, activity: activityResult.items, assets: assetsResult.items });
      setLoadError("");
    } catch (caught) {
      setLoadError(caught instanceof Error ? caught.message : t("home.loadError"));
    } finally {
      setLoading(false);
    }
  }, [t, workspace]);

  // Fetch workspace-scoped records after authentication state is available.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadDashboard(); }, [loadDashboard]);
  const recentItems = useMemo(() => dashboard ? buildHomeRecentItems(dashboard, 4) : [], [dashboard]);
  const hasWorkingGeneration = hasInFlightActivity(dashboard?.activity ?? []);
  const projects = dashboard?.projects ?? [];

  async function askTaslim() {
    const prompt = idea.trim();
    if (!prompt) {
      if (!files.length) router.push("/chat");
      setAskError(t("chat.sendError"));
      return;
    }
    if (!workspace) return;
    setAsking(true);
    setAskError("");
    try {
      const conversation = await api.createConversation(workspace.id, projectId ? { projectId } : {});
      const uploaded = await Promise.all(files.map((file) => api.uploadFile(workspace.id, file, { conversationId: conversation.id, projectId: projectId || undefined })));
      await api.sendMessage(conversation.id, prompt, undefined, uploaded.map((file) => file.id));
      router.push(`/chat/${conversation.id}`);
    } catch {
      setAskError(t("home.askError"));
    } finally {
      setAsking(false);
    }
  }

  return (
    <div className="home-dashboard">
      <section className="home-greeting" aria-labelledby="home-title">
        <p className="home-greeting-line">{t("home.greeting", { name: user?.displayName ?? "" })}</p>
        <h1 id="home-title">{t("home.mainQuestion")}</h1>
      </section>

      <section className="home-creation-surface" aria-labelledby="home-composer-title">
        <div className="home-creation-orbit home-creation-orbit-one" aria-hidden="true" />
        <div className="home-creation-orbit home-creation-orbit-two" aria-hidden="true" />
        <HomeComposer value={idea} onChange={(value) => { setIdea(value); if (askError) setAskError(""); }} onSubmit={() => void askTaslim()} busy={asking} projects={projects} projectId={projectId} onProjectChange={setProjectId} files={files} onFilesChange={(next) => setFiles((current) => [...current, ...next].slice(0, 5))} onRemoveFile={(index) => setFiles((current) => current.filter((_, itemIndex) => itemIndex !== index))} />
      </section>
      {askError && <p className="home-composer-error" role="alert">{askError}</p>}

      <div className="home-prompt-chips" aria-label={t("home.inspirationLabel")}>
        {promptChips.map((chip) => <button type="button" key={chip.labelKey} onClick={() => setIdea(t(chip.labelKey))} disabled={asking}><Sparkles size={12} />{t(chip.labelKey)}</button>)}
      </div>

      <section className="home-studios" aria-label={t("home.studiosTitle")}><StudioChooser compact /></section>

      <section className="home-continue" aria-labelledby="continue-work-title">
        <div className="home-section-heading home-continue-heading">
          <div><p className="home-section-kicker">{t("home.continueKicker")}</p><h2 id="continue-work-title">{t("home.continueSectionTitle")}</h2><p>{hasWorkingGeneration ? t("home.workInProgress") : t("home.continueSubtitle")}</p></div>
          <Link href="/activity" className="home-view-all">{t("home.viewAll")} <ArrowUpRight size={14} /></Link>
        </div>
        {loadError && <div className="inline-error home-dashboard-error" role="alert">{loadError}</div>}
        {loading ? <div className="home-recent-loading"><LoaderCircle className="home-button-spinner" size={21} /><span>{t("home.loadingWork")}</span></div> : recentItems.length ? <div className="home-recent-grid">{recentItems.map((item) => <RecentWorkCard key={item.id} item={item} locale={locale} t={t} />)}</div> : <div className="home-empty-work"><FolderKanban size={20} /><p>{t("home.noWorkDescription")}</p><Link href="/projects" className="home-empty-link">{t("home.viewAll")} <ArrowRight size={14} /></Link></div>}
      </section>
    </div>
  );
}

function RecentWorkCard({ item, locale, t }: { item: HomeRecentItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string }) {
  const iconByKind = { project: FolderKanban, conversation: MessageSquare, activity: Sparkles, asset: LibraryBig };
  const Icon = iconByKind[item.kind];
  const kindLabel = item.kind === "project" ? t("navigation.projects") : item.kind === "conversation" ? t("chat.title") : item.kind === "activity" ? t(`activity.type.${item.jobType ?? "other"}`) : t(`assets.type.${item.assetType ?? "other"}`);
  const statusLabel = item.status ? t(`activity.status.${item.status.toLowerCase()}`) : null;
  const statusClass = item.status ? `home-status-${item.status.toLowerCase()}` : "";
  const active = item.status === "Queued" || item.status === "Running";
  return <Link href={item.href} className={`home-recent-card home-recent-card-${item.kind}`}>
    <span className={`home-recent-icon home-recent-icon-${item.kind}`}><Icon size={17} /></span>
    <span className="home-recent-content"><span className="home-recent-meta"><span>{kindLabel}</span>{statusLabel && <span className={`home-work-status ${statusClass}`}>{statusLabel}</span>}</span><strong>{item.title}</strong><small>{formatDate(item.timestamp, locale)}{item.status && active && ` · ${item.progressPercent ?? 0}%`}</small>{active && <span className="home-progress"><span style={{ width: `${Math.max(0, Math.min(100, item.progressPercent ?? 0))}%` }} /></span>}</span>
    <ArrowUpRight className="home-recent-arrow" size={15} />
  </Link>;
}
